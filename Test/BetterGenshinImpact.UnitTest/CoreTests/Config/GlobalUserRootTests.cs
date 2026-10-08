using System.Text.Json;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoHoeing;
using Xunit;

namespace BetterGenshinImpact.UnitTest.CoreTests.Config;

[CollectionDefinition("GlobalUserRoot", DisableParallelization = true)]
public sealed class GlobalUserRootCollection { }

[Collection("GlobalUserRoot")]
public sealed class GlobalUserRootTests
{
    [Fact]
    public void UserPathsShareExplicitRoot_AssetsKeepPackageRoot_AndTraversalIsRejected()
    {
        var original = Global.StartUpPath;
        var package = Path.Combine(Path.GetTempPath(), "global-package-" + Guid.NewGuid().ToString("N"));
        var user = Path.Combine(Path.GetTempPath(), "global-user-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(package, "mistletoe-package.json"), JsonSerializer.Serialize(new
        {
            schema = "mistletoe.local-package", schemaVersion = 1, ipcIsolation = true, userRoot = user,
        }));
        try
        {
            Global.StartUpPath = package;
            Assert.Equal(user, Global.UserRoot);
            Assert.Equal(Path.Combine(user, "config.json"), Global.Absolute("User/config.json"));
            Assert.Equal(Path.Combine(user, "JsScript"), Global.ScriptPath());
            var hoeingPath = Path.Combine(user, "JsScript", "AutoHoeingOneDragon", "pathing");
            Directory.CreateDirectory(hoeingPath);
            Assert.Contains(hoeingPath, AutoHoeingTask.ResolveAllHoeingRouteDirs(new AutoHoeingConfig()));
            Assert.Equal(Path.Combine(user, "KeyMouseScript", "macro.json"), Global.Absolute("User\\KeyMouseScript\\macro.json"));
            Assert.Equal(Path.Combine(package, "Assets", "asset.json"), Global.Absolute(Path.Combine("Assets", "asset.json")));
            Assert.Throws<ArgumentException>(() => Global.Absolute("User/../outside.json"));
            Assert.False(Directory.Exists(Path.Combine(package, "User")));
        }
        finally { Global.StartUpPath = original; }
    }
}
