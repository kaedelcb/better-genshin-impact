using System.Windows;
using System.Windows.Media;
using System.Runtime.CompilerServices;

[assembly: DisableDpiAwareness]
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]

// 让单元测试项目可以访问 internal 成员（如 TeleportLoadingDetector）
// 详见 .kiro/specs/multiplayer-tp-success-via-loading-screen/design.md §3.5
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("BetterGenshinImpact.UnitTest")]

[assembly: InternalsVisibleTo("BetterGenshinImpact.UnitTest")]
