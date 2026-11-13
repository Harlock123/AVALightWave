using AVAModeler.Core;
using AVAModeler.Scene;

namespace AVALightWave.Core.Tests;

/// <summary>Camera bookmarks: saved with the scene, recalled with or without keys, each one undo step.</summary>
public class BookmarkTests
{
    [Fact]
    public void A_bookmark_is_saved_recalled_and_undone()
    {
        var d = new SceneDocument();
        var s = d.Session;
        var cam = s.Scene.CurrentCamera!;
        void Place(Vec3 p)
        {
            for (int i = 0; i < 3; i++) cam.Channels[i].SetConstant(p[i]);
        }
        Place(new Vec3(1, 2, -5));
        for (int i = 0; i < 3; i++) cam.Channels[3 + i].SetConstant(new[] { 10.0, 15, 0 }[i]);
        cam.Zoom.SetConstant(2.5);
        CameraBookmarks.Save(s, "Wide", cam, 1);
        Assert.Equal("Bookmark Wide", s.History.UndoLabel);
        Assert.Single(CameraBookmarks.List(s.Scene));

        // Moved elsewhere (not animated), recall puts it back without keys.
        Place(new Vec3(9, 9, 9));
        cam.Zoom.SetConstant(4);
        CameraBookmarks.Recall(s, "wide", cam, 20);
        Assert.Equal(new Vec3(1, 2, -5), cam.PositionAt(20));
        Assert.Equal(2.5, cam.Zoom.Evaluate(1), 9);
        Assert.False(AVAModeler.Layout.Keying.IsAnimated(cam.Channels[0]));

        // Animated: recall keys it at the frame.
        cam.SetPosition(1, new Vec3(0, 0, 0));
        cam.SetPosition(30, new Vec3(3, 0, 0));
        CameraBookmarks.Recall(s, "Wide", cam, 40);
        Assert.Equal(1, cam.PositionAt(40).X, 9);
        Assert.Equal(3, cam.PositionAt(30).X, 9);

        // Kept in the scene file.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "b.avascene");
            d.Save(path);
            var e = new SceneDocument();
            e.Load(path);
            Assert.Equal("Wide", CameraBookmarks.List(e.Scene).Single().Name);
        }
        finally { dir.Delete(true); }

        CameraBookmarks.Remove(s, "Wide");
        Assert.Empty(CameraBookmarks.List(s.Scene));
        s.Undo();
        Assert.Single(CameraBookmarks.List(s.Scene));
        Assert.Contains("no bookmark \"Close\"", Assert.Throws<ArgumentException>(() => CameraBookmarks.Recall(s, "Close", cam, 1)).Message);
        Assert.Throws<ArgumentException>(() => CameraBookmarks.Save(s, " ", cam, 1));
    }
}
