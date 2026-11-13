using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVAModeler.Layout;
using AVAModeler.Mcp;
using AVAModeler.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVALightWave.Mcp;

/// <summary>
/// A host that runs work at once on the caller's thread, against a given command API and scene: for calling the
/// engine's tools from inside another tool's work (which already holds the real host). A scene change made through it
/// inside a <see cref="LayoutSession.Edit{T}"/> becomes part of that edit, so many calls are one undo step, all or
/// nothing.
/// </summary>
internal sealed class InlineHost(string root, Commands commands, LayoutSession? layout) : IModelerHost
{
    public string Root { get; } = root;

    public Task<T> RunAsync<T>(string action, Func<Commands, T> work, CancellationToken cancel = default) =>
        Task.FromResult(work(commands));

    public Task<T> RunSceneAsync<T>(string action, Func<Commands, LayoutSession, T> work, CancellationToken cancel = default) =>
        Task.FromResult(work(commands, layout ?? throw new InvalidOperationException("There's no scene here: only modeling calls can be used.")));
}

/// <summary>
/// Calls the engine's MCP tools (or AVALightWave's) in process by their MCP name, with JSON arguments bound to the
/// handler's parameters as the server binds them: for steps in <c>build_object</c>, <c>build_scene</c> and the like.
/// Only the names it's given can be called, and only handlers that finish at once (the inline host makes the scene
/// ones do; renders don't, and aren't offered).
/// </summary>
internal sealed class EngineCaller
{
    private readonly Dictionary<string, (MethodInfo Method, object Target)> _tools = new(StringComparer.Ordinal);

    public EngineCaller(object target, IEnumerable<string> names) => Add(target, names);

    public void Add(object target, IEnumerable<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        foreach (var m in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.GetCustomAttribute<McpServerToolAttribute>()?.Name is { } name && wanted.Contains(name))
                _tools[name] = (m, target);
    }

    public IEnumerable<string> Names => _tools.Keys.Order(StringComparer.Ordinal);

    public bool Has(string name) => _tools.ContainsKey(name);

    /// <summary>Calls a tool; returns its text (JSON for most). Its failure is thrown with its own message.</summary>
    public string Call(string name, JsonObject? args)
    {
        if (!_tools.TryGetValue(name, out var tool))
            throw new ArgumentException($"\"{name}\" can't be used here. These can: {string.Join(", ", Names)}.");
        var parameters = tool.Method.GetParameters();
        var given = args ?? [];
        var known = parameters.Where(p => p.ParameterType != typeof(CancellationToken)).Select(p => p.Name!).ToList();
        foreach (var (key, _) in given)
            if (!known.Contains(key))
                throw new ArgumentException($"{name} has no argument \"{key}\". It takes: {(known.Count == 0 ? "none" : string.Join(", ", known))}.");
        var values = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (p.ParameterType == typeof(CancellationToken)) values[i] = CancellationToken.None;
            else if (given[p.Name!] is { } node)
            {
                try
                {
                    values[i] = node.Deserialize(p.ParameterType);
                }
                catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or FormatException)
                {
                    throw new ArgumentException($"{name}: {p.Name} must be {Kind(p.ParameterType)}, not {node.ToJsonString()}.");
                }
            }
            else if (p.HasDefaultValue) values[i] = p.DefaultValue;
            else if (Nullable.GetUnderlyingType(p.ParameterType) != null || !p.ParameterType.IsValueType && IsNullable(p)) values[i] = null;
            else throw new ArgumentException($"{name} needs {p.Name}.");
        }
        object? result;
        try
        {
            result = tool.Method.Invoke(tool.Target, values);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            throw Unwrap(e.InnerException);
        }
        if (result is Task task)
        {
            if (!task.IsCompleted) throw new InvalidOperationException($"{name} can't be used here: it doesn't finish at once.");
            if (task.IsFaulted) throw Unwrap(task.Exception!.InnerException ?? task.Exception);
            result = task.GetType().GetProperty("Result")?.GetValue(task);
        }
        return result switch
        {
            string s => s,
            CallToolResult r when r.IsError == true => throw new McpException(Text(r)),
            CallToolResult r => Text(r),
            null => "",
            _ => result.ToString() ?? "",
        };
    }

    /// <summary>A tool's text result as JSON, or {"result": text} when it isn't an object.</summary>
    public static JsonObject AsObject(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is JsonObject o) return o;
        }
        catch (JsonException)
        {
        }
        return new JsonObject { ["result"] = text };
    }

    private static string Text(CallToolResult r) => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static Exception Unwrap(Exception e) => e is AggregateException { InnerException: { } inner } ? Unwrap(inner) : e;

    private static bool IsNullable(ParameterInfo p) => new NullabilityInfoContext().Create(p).WriteState == NullabilityState.Nullable;

    private static string Kind(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t == typeof(string) ? "text" : t == typeof(bool) ? "true or false" : t == typeof(int) ? "a whole number" : t == typeof(double) ? "a number"
            : t == typeof(double[]) ? "a list of numbers" : t == typeof(double[][]) ? "a list of [x, y, z] lists" : t == typeof(int[][]) ? "a list of lists of whole numbers"
            : t.IsArray ? "a list" : "an object";
    }
}
