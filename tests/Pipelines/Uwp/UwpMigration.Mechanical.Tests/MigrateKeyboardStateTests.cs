// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UwpMigration.Mechanical.Tests;

public partial class MigrateCommandTests
{
    [TestMethod]
    public async Task Migrate_KeyboardStateRewritesPlainAndQualifiedCallsAndCountsFiles()
    {
        var source = await CreateUwpSourceAsync("KeyboardStateApp");
        await WriteAsync(source, "Plain.cs", """
            using Windows.UI.Core;
            namespace KeyboardStateApp;
            public sealed class Plain
            {
                object First(VirtualKey key) =>
                    CoreWindow.GetForCurrentThread().GetKeyState(key);
                object Second(VirtualKey key) =>
                    CoreWindow . GetForCurrentThread ( ) . GetKeyState (Normalize(key));
            }
            """);
        await WriteAsync(source, "Qualified.cs", """
            namespace KeyboardStateApp;
            public sealed class Qualified
            {
                object First(VirtualKey key) =>
                    Windows.UI.Core. CoreWindow.GetForCurrentThread().GetKeyState(key);
                object Second(VirtualKey key) =>
                    global:: Windows.UI.Core.CoreWindow.GetForCurrentThread().GetKeyState(GetKey(key, 2));
            }
            """);
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "keyboard-state-output"));
        ArrangeTemplateCreation(target, "KeyboardStateApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(
            output,
            "Rewrote 4 CoreWindow key-state call(s) in 2 .cs file(s)");
        var plain = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "Plain.cs"),
            TestContext.CancellationToken);
        var qualified = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "Qualified.cs"),
            TestContext.CancellationToken);
        Assert.AreEqual(
            2,
            CountOccurrences(
                plain,
                "Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread"));
        Assert.AreEqual(
            2,
            CountOccurrences(
                qualified,
                "Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread"));
        StringAssert.Contains(plain, "GetKeyStateForCurrentThread(key)");
        StringAssert.Contains(plain, "GetKeyStateForCurrentThread (Normalize(key))");
        StringAssert.Contains(qualified, "GetKeyStateForCurrentThread(GetKey(key, 2))");
        Assert.IsFalse(
            qualified.Contains(
                "global:: Microsoft.UI.Input",
                StringComparison.Ordinal));
        Assert.IsFalse(
            qualified.Contains(
                "Windows.UI.Core. Microsoft.UI.Input",
                StringComparison.Ordinal));

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        var transform = report.RootElement.GetProperty("transforms")
            .EnumerateArray()
            .Single(item =>
                item.GetProperty("id").GetString() == "UWMIG-KEY-STATE");
        Assert.AreEqual(2, transform.GetProperty("changedFiles").GetInt32());
        Assert.IsFalse(report.RootElement.GetProperty("todos")
            .EnumerateArray()
            .Any(todo => todo.GetProperty("id").GetString() == "UWMIG015"));
    }

    [TestMethod]
    public async Task Migrate_KeyboardStateSkipsLexicalAliasesAndShadowedReceiversAndReportsResiduals()
    {
        var source = await CreateUwpSourceAsync("KeyboardResidualApp");
        await WriteAsync(source, "Lexical.cs", """""
            using Windows.UI.Core;
            namespace KeyboardResidualApp;
            public sealed class Lexical
            {
                // CoreWindow.GetForCurrentThread().GetKeyState(key);
                /* Windows.UI.Core.CoreWindow.GetForCurrentThread().GetKeyState(key); */
                const string Regular = "CoreWindow.GetForCurrentThread().GetKeyState(key)";
                const string Verbatim = @"CoreWindow.GetForCurrentThread().GetKeyState(key)";
                const string Interpolated = $"CoreWindow.GetForCurrentThread().GetKeyState({1})";
                const string NestedInterpolated = $"{Echo("CoreWindow.GetForCurrentThread().GetKeyState(key)")}";
                const string Raw = """
                    Windows.UI.Core.CoreWindow.GetForCurrentThread().GetKeyState(key)
                    """;
            }
            """"");
        await WriteAsync(source, "Aliases.cs", """
            using CoreWindow = Windows.UI.Core.CoreWindow;
            using UwpCore = Windows.UI.Core;
            namespace KeyboardResidualApp;
            public sealed class Aliases
            {
                object First(VirtualKey key) =>
                    CoreWindow.GetForCurrentThread().GetKeyState(key);
                object Second(VirtualKey key) =>
                    UwpCore.CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        await WriteAsync(source, "Shadowed.cs", """
            using Windows.UI.Core;
            namespace KeyboardResidualApp;
            public sealed class CoreWindow
            {
                public static CoreWindow GetForCurrentThread() => new();
                public object GetKeyState(object key) => key;
            }
            public sealed class Shadowed
            {
                object Read(object key) =>
                    CoreWindow.GetForCurrentThread().GetKeyState(key);
                object Explicit(VirtualKey key) =>
                    global::Windows.UI.Core.CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        await WriteAsync(source, "Unrelated.cs", """
            using CoreWindow = Contoso.CoreWindow;
            namespace KeyboardResidualApp;
            public sealed class Unrelated
            {
                object Read(object key) =>
                    CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        await WriteAsync(source, "WindowsAlias.cs", """
            using Windows = Contoso.Windows;
            namespace KeyboardResidualApp;
            public sealed class WindowsAlias
            {
                object Read(object key) =>
                    Windows.UI.Core.CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        await WriteAsync(source, "InlineComment.cs", """
            using Windows.UI.Core;
            namespace KeyboardResidualApp;
            public sealed class InlineComment
            {
                object Read(VirtualKey key) =>
                    CoreWindow /* preserve receiver comment */ .GetForCurrentThread().GetKeyState(key);
            }
            """);
        await WriteAsync(source, "OtherCoreWindow.cs", """
            namespace KeyboardResidualApp;
            public sealed class OtherCoreWindow
            {
                object Bounds() =>
                    Windows.UI.Core.CoreWindow.GetForCurrentThread().Bounds;
            }
            """);
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "keyboard-residual-output"));
        ArrangeTemplateCreation(target, "KeyboardResidualApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var lexical = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "Lexical.cs"),
            TestContext.CancellationToken);
        var aliases = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "Aliases.cs"),
            TestContext.CancellationToken);
        var shadowed = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "Shadowed.cs"),
            TestContext.CancellationToken);
        var unrelated = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "Unrelated.cs"),
            TestContext.CancellationToken);
        var windowsAlias = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "WindowsAlias.cs"),
            TestContext.CancellationToken);
        var inlineComment = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "InlineComment.cs"),
            TestContext.CancellationToken);
        Assert.AreEqual(7, CountOccurrences(lexical, "GetForCurrentThread().GetKeyState"));
        StringAssert.Contains(
            lexical,
            "$\"{Echo(\"CoreWindow.GetForCurrentThread().GetKeyState(key)\")}\"");
        Assert.AreEqual(2, CountOccurrences(aliases, "GetForCurrentThread().GetKeyState"));
        Assert.AreEqual(1, CountOccurrences(shadowed, "GetForCurrentThread().GetKeyState"));
        Assert.AreEqual(
            1,
            CountOccurrences(
                shadowed,
                "Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread"));
        Assert.AreEqual(1, CountOccurrences(unrelated, "GetForCurrentThread().GetKeyState"));
        Assert.AreEqual(1, CountOccurrences(windowsAlias, "GetForCurrentThread().GetKeyState"));
        StringAssert.Contains(inlineComment, "/* preserve receiver comment */");
        Assert.AreEqual(1, CountOccurrences(inlineComment, "GetForCurrentThread().GetKeyState"));

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        var todo = report.RootElement.GetProperty("todos")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "UWMIG015");
        Assert.AreEqual("core-window", todo.GetProperty("category").GetString());
        Assert.AreEqual(
            "core-window-usage-requires-semantic-port",
            todo.GetProperty("guidance").GetProperty("reasonCode").GetString());
        Assert.IsTrue(todo.GetProperty("guidance")
            .GetProperty("facts")
            .EnumerateArray()
            .Any(fact =>
                fact.GetProperty("id").GetString() ==
                "core-window-residual-detected"));
        Assert.IsTrue(todo.GetProperty("guidance")
            .GetProperty("candidateStrategies")
            .GetArrayLength() >= 2);
        Assert.IsTrue(todo.GetProperty("guidance")
            .GetProperty("forbiddenShortcuts")
            .GetArrayLength() >= 3);
        Assert.IsTrue(todo.GetProperty("guidance")
            .GetProperty("completionConditions")
            .GetArrayLength() >= 3);
        var paths = todo.GetProperty("locations")
            .EnumerateArray()
            .Select(location => location.GetProperty("path").GetString())
            .ToArray();
        CollectionAssert.Contains(paths, "Aliases.cs");
        CollectionAssert.Contains(paths, "Shadowed.cs");
        CollectionAssert.Contains(paths, "InlineComment.cs");
        CollectionAssert.Contains(paths, "OtherCoreWindow.cs");
        CollectionAssert.DoesNotContain(paths, "Lexical.cs");
        CollectionAssert.DoesNotContain(paths, "Unrelated.cs");
        CollectionAssert.DoesNotContain(paths, "WindowsAlias.cs");
        Assert.IsFalse(report.RootElement.GetProperty("todos")
            .EnumerateArray()
            .Any(item => item.GetProperty("id").GetString() is
                "UWMIG005" or "UWMIG007" or "UWMIG009"));
    }

    [TestMethod]
    public async Task Migrate_KeyboardStateUsesProjectWideCoreWindowIdentityAndGlobalNamespaceImport()
    {
        var source = await CreateUwpSourceAsync("ProjectIdentityKeyboardApp");
        await WriteAsync(source, "ApplicationCoreWindow.cs", """
            namespace Shared.Windowing;
            public sealed class CoreWindow
            {
                public static CoreWindow GetForCurrentThread() => new();
                public object GetKeyState(object key) => key;
            }
            """);
        await WriteAsync(source, "AmbiguousConsumer.cs", """
            using Windows.UI.Core;
            using Shared.Windowing;
            namespace ProjectIdentityKeyboardApp;
            public sealed class AmbiguousConsumer
            {
                object Ambiguous(object key) =>
                    CoreWindow.GetForCurrentThread().GetKeyState(key);
                object Explicit(VirtualKey key) =>
                    global::Windows.UI.Core.CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        await WriteAsync(source, "GlobalImportConsumer.cs", """
            using global::Windows.UI.Core;
            namespace ProjectIdentityKeyboardApp;
            public sealed class GlobalImportConsumer
            {
                object Read(VirtualKey key) =>
                    CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "project-identity-keyboard-output"));
        ArrangeTemplateCreation(target, "ProjectIdentityKeyboardApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var ambiguous = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "AmbiguousConsumer.cs"),
            TestContext.CancellationToken);
        var globalImport = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "GlobalImportConsumer.cs"),
            TestContext.CancellationToken);
        Assert.AreEqual(
            1,
            CountOccurrences(
                ambiguous,
                "CoreWindow.GetForCurrentThread().GetKeyState"));
        Assert.AreEqual(
            1,
            CountOccurrences(
                ambiguous,
                "Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread"));
        Assert.AreEqual(
            1,
            CountOccurrences(
                globalImport,
                "Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread"));

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        var todo = report.RootElement.GetProperty("todos")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "UWMIG015");
        Assert.IsTrue(todo.GetProperty("locations")
            .EnumerateArray()
            .Any(location =>
                location.GetProperty("path").GetString() ==
                "AmbiguousConsumer.cs"));
        Assert.IsFalse(todo.GetProperty("locations")
            .EnumerateArray()
            .Any(location =>
                location.GetProperty("path").GetString() ==
                "GlobalImportConsumer.cs"));
    }

    [TestMethod]
    public async Task Migrate_KeyboardStateHonorsProjectWideGlobalAliases()
    {
        var source = await CreateUwpSourceAsync("GlobalAliasKeyboardApp");
        await WriteAsync(source, "GlobalAliases.cs", """
            global using CoreWindow = Shared.Windowing.KeyboardFacade;
            global using UwpCore = global::Windows.UI.Core;
            global using UwpCoreWindow = global::Windows.UI.Core.CoreWindow;
            """);
        await WriteAsync(source, "KeyboardFacade.cs", """
            namespace Shared.Windowing;
            public sealed class KeyboardFacade
            {
                public static KeyboardFacade GetForCurrentThread() => new();
                public object GetKeyState(object key) => key;
            }
            """);
        await WriteAsync(source, "Consumer.cs", """
            using Windows.UI.Core;
            namespace GlobalAliasKeyboardApp;
            public sealed class Consumer
            {
                object Ambiguous(object key) =>
                    CoreWindow.GetForCurrentThread().GetKeyState(key);
                object NamespaceAlias(VirtualKey key) =>
                    UwpCore.CoreWindow.GetForCurrentThread().GetKeyState(key);
                object TypeAlias(VirtualKey key) =>
                    UwpCoreWindow.GetForCurrentThread().GetKeyState(key);
                object Explicit(VirtualKey key) =>
                    global::Windows.UI.Core.CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "global-alias-keyboard-output"));
        ArrangeTemplateCreation(target, "GlobalAliasKeyboardApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var consumer = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "Consumer.cs"),
            TestContext.CancellationToken);
        Assert.AreEqual(
            3,
            CountOccurrences(
                consumer,
                "GetForCurrentThread().GetKeyState"));
        Assert.AreEqual(
            1,
            CountOccurrences(
                consumer,
                "Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread"));

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        var todo = report.RootElement.GetProperty("todos")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "UWMIG015");
        var consumerLocations = todo.GetProperty("locations")
            .EnumerateArray()
            .Where(location =>
                location.GetProperty("path").GetString() == "Consumer.cs")
            .ToArray();
        Assert.HasCount(3, consumerLocations);
    }

    [TestMethod]
    public async Task Migrate_KeyboardStateAppliesProjectWideGlobalUwpImport()
    {
        var source = await CreateUwpSourceAsync("GlobalImportKeyboardApp");
        await WriteAsync(source, "GlobalUsings.cs", """
            global using Windows.UI.Core;
            """);
        await WriteAsync(source, "Consumer.cs", """
            namespace GlobalImportKeyboardApp;
            public sealed class Consumer
            {
                object Read(VirtualKey key) =>
                    CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "global-import-keyboard-output"));
        ArrangeTemplateCreation(target, "GlobalImportKeyboardApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var consumer = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "Consumer.cs"),
            TestContext.CancellationToken);
        StringAssert.Contains(
            consumer,
            "Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)");
        Assert.IsFalse(
            consumer.Contains(
                "CoreWindow.GetForCurrentThread().GetKeyState",
                StringComparison.Ordinal));

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        Assert.IsFalse(report.RootElement.GetProperty("todos")
            .EnumerateArray()
            .Any(item => item.GetProperty("id").GetString() == "UWMIG015"));
    }

    [TestMethod]
    public async Task Migrate_KeyboardStateProcessesInterpolationExpressionsButNotNestedStringData()
    {
        var source = await CreateUwpSourceAsync("InterpolatedKeyboardApp");
        await WriteAsync(source, "Interpolated.cs", """""
            using Windows.UI.Core;
            namespace InterpolatedKeyboardApp;
            public sealed class Interpolated
            {
                string Normal(VirtualKey key) =>
                    $"{CoreWindow.GetForCurrentThread().GetKeyState(key)}";
                string Verbatim(VirtualKey key) =>
                    $@"{CoreWindow.GetForCurrentThread().GetKeyState(key)}";
                string Raw(VirtualKey key) =>
                    $"""{CoreWindow.GetForCurrentThread().GetKeyState(key)}""";
                string RawWithTwoDollars(VirtualKey key) =>
                    $$"""{{CoreWindow.GetForCurrentThread().GetKeyState(key)}}""";
                string Conditional(bool flag, VirtualKey key) =>
                    $"{(flag ? null : CoreWindow.GetForCurrentThread().GetKeyState(key))}";
                string ParenthesizedGlobal(VirtualKey key) =>
                    $"{(global::Windows.UI.Core.CoreWindow.GetForCurrentThread().GetKeyState(key))}";
                string NestedData() =>
                    $"{Echo("CoreWindow.GetForCurrentThread().GetKeyState(key)")}";
                string FormatData() =>
                    $"{42:CoreWindow.GetForCurrentThread().GetKeyState(key)}";
            }
            """"");
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "interpolated-keyboard-output"));
        ArrangeTemplateCreation(target, "InterpolatedKeyboardApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var migrated = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "Interpolated.cs"),
            TestContext.CancellationToken);
        Assert.AreEqual(
            6,
            CountOccurrences(
                migrated,
                "Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread"));
        Assert.AreEqual(
            2,
            CountOccurrences(
                migrated,
                "CoreWindow.GetForCurrentThread().GetKeyState"));
        StringAssert.Contains(
            migrated,
            "Echo(\"CoreWindow.GetForCurrentThread().GetKeyState(key)\")");

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        Assert.IsFalse(report.RootElement.GetProperty("todos")
            .EnumerateArray()
            .Any(item => item.GetProperty("id").GetString() == "UWMIG015"));
    }

    [TestMethod]
    public async Task Migrate_KeyboardStateAccountsForPartialAndInheritedCoreWindowMembers()
    {
        var source = await CreateUwpSourceAsync("PartialKeyboardApp");
        await WriteAsync(source, "Consumer.Member.cs", """
            namespace PartialKeyboardApp;
            public sealed partial class Consumer
            {
                private object CoreWindow => new();
            }
            """);
        await WriteAsync(source, "Consumer.Call.cs", """
            using Windows.UI.Core;
            namespace PartialKeyboardApp;
            public sealed partial class Consumer
            {
                object Ambiguous(VirtualKey key) =>
                    CoreWindow.GetForCurrentThread().GetKeyState(key);
                object Explicit(VirtualKey key) =>
                    global::Windows.UI.Core.CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        await WriteAsync(source, "BaseWithCoreWindow.cs", """
            namespace PartialKeyboardApp;
            public class BaseWithCoreWindow
            {
                protected object CoreWindow => new();
            }
            """);
        await WriteAsync(source, "DerivedConsumer.cs", """
            using Windows.UI.Core;
            namespace PartialKeyboardApp;
            public sealed class DerivedConsumer : BaseWithCoreWindow
            {
                object Ambiguous(VirtualKey key) =>
                    CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        await WriteAsync(source, "SharedBase.cs", """
            namespace Shared;
            public class Base
            {
                protected object CoreWindow => new();
            }
            """);
        await WriteAsync(source, "OtherBase.cs", """
            namespace Other;
            public class Base
            {
            }
            """);
        await WriteAsync(source, "ImportedBaseConsumer.cs", """
            using Windows.UI.Core;
            using Shared;
            namespace PartialKeyboardApp;
            public sealed class ImportedBaseConsumer : Base
            {
                object Ambiguous(VirtualKey key) =>
                    CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        await WriteAsync(source, "AmbiguousBaseConsumer.cs", """
            using Windows.UI.Core;
            using Shared;
            using Other;
            namespace PartialKeyboardApp;
            public sealed class AmbiguousBaseConsumer : Base
            {
                object Ambiguous(VirtualKey key) =>
                    CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        await WriteAsync(source, "UnresolvedBaseConsumer.cs", """
            using Windows.UI.Core;
            namespace PartialKeyboardApp;
            public sealed class UnresolvedBaseConsumer : MissingExternalBase
            {
                object Ambiguous(VirtualKey key) =>
                    CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        await WriteAsync(source, "GenuineConsumer.cs", """
            using Windows.UI.Core;
            namespace PartialKeyboardApp;
            public sealed class GenuineConsumer
            {
                object Read(VirtualKey key) =>
                    CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "partial-keyboard-output"));
        ArrangeTemplateCreation(target, "PartialKeyboardApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var partialConsumer = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "Consumer.Call.cs"),
            TestContext.CancellationToken);
        var derivedConsumer = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "DerivedConsumer.cs"),
            TestContext.CancellationToken);
        var genuineConsumer = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "GenuineConsumer.cs"),
            TestContext.CancellationToken);
        var importedBaseConsumer = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "ImportedBaseConsumer.cs"),
            TestContext.CancellationToken);
        var ambiguousBaseConsumer = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "AmbiguousBaseConsumer.cs"),
            TestContext.CancellationToken);
        var unresolvedBaseConsumer = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "UnresolvedBaseConsumer.cs"),
            TestContext.CancellationToken);
        Assert.AreEqual(
            1,
            CountOccurrences(
                partialConsumer,
                "CoreWindow.GetForCurrentThread().GetKeyState"));
        Assert.AreEqual(
            1,
            CountOccurrences(
                partialConsumer,
                "Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread"));
        Assert.AreEqual(
            1,
            CountOccurrences(
                derivedConsumer,
                "CoreWindow.GetForCurrentThread().GetKeyState"));
        Assert.AreEqual(
            1,
            CountOccurrences(
                genuineConsumer,
                "Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread"));
        Assert.AreEqual(
            1,
            CountOccurrences(
                importedBaseConsumer,
                "CoreWindow.GetForCurrentThread().GetKeyState"));
        Assert.AreEqual(
            1,
            CountOccurrences(
                ambiguousBaseConsumer,
                "CoreWindow.GetForCurrentThread().GetKeyState"));
        Assert.AreEqual(
            1,
            CountOccurrences(
                unresolvedBaseConsumer,
                "Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread"));

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        var todo = report.RootElement.GetProperty("todos")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "UWMIG015");
        var paths = todo.GetProperty("locations")
            .EnumerateArray()
            .Select(location => location.GetProperty("path").GetString())
            .ToArray();
        CollectionAssert.Contains(paths, "Consumer.Call.cs");
        CollectionAssert.Contains(paths, "DerivedConsumer.cs");
        CollectionAssert.Contains(paths, "ImportedBaseConsumer.cs");
        CollectionAssert.Contains(paths, "AmbiguousBaseConsumer.cs");
        CollectionAssert.DoesNotContain(paths, "UnresolvedBaseConsumer.cs");
        CollectionAssert.DoesNotContain(paths, "GenuineConsumer.cs");
    }

    [TestMethod]
    public async Task Migrate_KeyboardStateRewritesNotifyPropertyChangedViewModelCall()
    {
        var source = await CreateUwpSourceAsync("VanArsdelKeyboardApp");
        await WriteAsync(source, "ProductConfigurationViewModel.cs", """
            using System.ComponentModel;
            using Windows.System;
            using Windows.UI.Core;
            namespace VanArsdel;
            public sealed class ProductConfigurationViewModel : INotifyPropertyChanged
            {
                public event PropertyChangedEventHandler? PropertyChanged;

                public bool IsControlDown() =>
                    CoreWindow.GetForCurrentThread().GetKeyState(VirtualKey.Control)
                        .HasFlag(CoreVirtualKeyStates.Down);
            }
            """);
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "vanarsdel-keyboard-output"));
        ArrangeTemplateCreation(target, "VanArsdelKeyboardApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var migrated = await File.ReadAllTextAsync(
            Path.Combine(
                target.FullName,
                "ProductConfigurationViewModel.cs"),
            TestContext.CancellationToken);
        StringAssert.Contains(
            migrated,
            "Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)");

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        var transform = report.RootElement.GetProperty("transforms")
            .EnumerateArray()
            .Single(item =>
                item.GetProperty("id").GetString() == "UWMIG-KEY-STATE");
        Assert.AreEqual(1, transform.GetProperty("changedFiles").GetInt32());
        Assert.IsFalse(report.RootElement.GetProperty("todos")
            .EnumerateArray()
            .Any(item => item.GetProperty("id").GetString() == "UWMIG015"));
    }

    [TestMethod]
    public async Task Migrate_KeyboardStateResolvesRelativeQualifiedWindowsNamespaceSafely()
    {
        var source = await CreateUwpSourceAsync("QualifiedBindingKeyboardApp");
        await WriteAsync(source, "ApplicationCoreWindow.cs", """
            namespace Contoso.Windows.UI.Core;
            public sealed class CoreWindow
            {
                public static CoreWindow GetForCurrentThread() => new();
                public object GetKeyState(object key) => key;
            }
            """);
        await WriteAsync(source, "ContosoConsumer.cs", """
            namespace Contoso;
            public sealed class ContosoConsumer
            {
                object Relative(object key) =>
                    Windows.UI.Core.CoreWindow.GetForCurrentThread().GetKeyState(key);
                object Global(VirtualKey key) =>
                    global::Windows.UI.Core.CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        await WriteAsync(source, "NestedConsumer.cs", """
            namespace Contoso.Features.Deep;
            public sealed class NestedConsumer
            {
                object Relative(object key) =>
                    Windows.UI.Core.CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        await WriteAsync(source, "SafeConsumer.cs", """
            namespace Fabrikam;
            public sealed class SafeConsumer
            {
                object Read(VirtualKey key) =>
                    Windows.UI.Core.CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "qualified-binding-keyboard-output"));
        ArrangeTemplateCreation(target, "QualifiedBindingKeyboardApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var contoso = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "ContosoConsumer.cs"),
            TestContext.CancellationToken);
        var nested = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "NestedConsumer.cs"),
            TestContext.CancellationToken);
        var safe = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "SafeConsumer.cs"),
            TestContext.CancellationToken);
        Assert.AreEqual(
            1,
            CountOccurrences(
                contoso,
                "Windows.UI.Core.CoreWindow.GetForCurrentThread().GetKeyState"));
        Assert.AreEqual(
            1,
            CountOccurrences(
                contoso,
                "Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread"));
        Assert.AreEqual(
            1,
            CountOccurrences(
                nested,
                "Windows.UI.Core.CoreWindow.GetForCurrentThread().GetKeyState"));
        Assert.AreEqual(
            1,
            CountOccurrences(
                safe,
                "Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread"));

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        var todo = report.RootElement.GetProperty("todos")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "UWMIG015");
        var paths = todo.GetProperty("locations")
            .EnumerateArray()
            .Select(location => location.GetProperty("path").GetString())
            .ToArray();
        CollectionAssert.Contains(paths, "ContosoConsumer.cs");
        CollectionAssert.Contains(paths, "NestedConsumer.cs");
        CollectionAssert.DoesNotContain(paths, "SafeConsumer.cs");
    }

    [TestMethod]
    public async Task Migrate_KeyboardStateDoesNotAssumeGlobalNamespaceTypeIsPlatformOwned()
    {
        var source = await CreateUwpSourceAsync("RootWindowsKeyboardApp");
        await WriteAsync(source, "ApplicationCoreWindow.cs", """
            namespace Windows.UI.Core;
            public sealed class CoreWindow
            {
                public static CoreWindow GetForCurrentThread() => new();
                public object GetKeyState(object key) => key;
            }
            """);
        await WriteAsync(source, "Consumer.cs", """
            using Windows.UI.Core;
            namespace Fabrikam;
            public sealed class Consumer
            {
                object Plain(object key) =>
                    CoreWindow.GetForCurrentThread().GetKeyState(key);
                object Relative(object key) =>
                    Windows.UI.Core.CoreWindow.GetForCurrentThread().GetKeyState(key);
                object Global(object key) =>
                    global::Windows.UI.Core.CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "root-windows-keyboard-output"));
        ArrangeTemplateCreation(target, "RootWindowsKeyboardApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var consumer = await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "Consumer.cs"),
            TestContext.CancellationToken);
        Assert.AreEqual(
            3,
            CountOccurrences(
                consumer,
                "GetForCurrentThread().GetKeyState"));
        Assert.AreEqual(
            0,
            CountOccurrences(
                consumer,
                "Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread"));

        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            TestContext.CancellationToken));
        var todo = report.RootElement.GetProperty("todos")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "UWMIG015");
        var consumerLocations = todo.GetProperty("locations")
            .EnumerateArray()
            .Where(location =>
                location.GetProperty("path").GetString() == "Consumer.cs")
            .ToArray();
        Assert.HasCount(3, consumerLocations);
    }

    [TestMethod]
    public async Task Migrate_KeyboardStatePreservesUtf16Encoding()
    {
        var source = await CreateUwpSourceAsync("Utf16KeyboardStateApp");
        var sourcePath = Path.Combine(source.FullName, "Keyboard.cs");
        const string code = """
            using Windows.UI.Core;
            namespace Utf16KeyboardStateApp;
            public sealed class Keyboard
            {
                object Read(VirtualKey key) =>
                    CoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """;
        await File.WriteAllTextAsync(
            sourcePath,
            code,
            new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            TestContext.CancellationToken);
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "utf16-keyboard-state-output"));
        ArrangeTemplateCreation(target, "Utf16KeyboardStateApp");

        var (exit, output) = await InvokeAsync(source, target);

        Assert.AreEqual(0, exit, output);
        var targetPath = Path.Combine(target.FullName, "Keyboard.cs");
        var bytes = await File.ReadAllBytesAsync(
            targetPath,
            TestContext.CancellationToken);
        CollectionAssert.AreEqual(Encoding.Unicode.GetPreamble(), bytes[..2]);
        var migrated = await File.ReadAllTextAsync(
            targetPath,
            TestContext.CancellationToken);
        StringAssert.Contains(
            migrated,
            "Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)");
    }

    [TestMethod]
    public async Task Verify_KeyboardStateResidualIsIdempotentAndPreservesStatus()
    {
        var source = await CreateUwpSourceAsync("VerifyKeyboardStateApp");
        await WriteAsync(source, "Keyboard.cs", """
            using UwpCoreWindow = Windows.UI.Core.CoreWindow;
            namespace VerifyKeyboardStateApp;
            public sealed class Keyboard
            {
                object Read(VirtualKey key) =>
                    UwpCoreWindow.GetForCurrentThread().GetKeyState(key);
            }
            """);
        var target = new DirectoryInfo(Path.Combine(
            _tempDirectory.FullName,
            "verify-keyboard-state-output"));
        ArrangeTemplateCreation(target, "VerifyKeyboardStateApp");
        var (migrateExit, migrateOutput) = await InvokeAsync(source, target);
        Assert.AreEqual(0, migrateExit, migrateOutput);

        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        var reportNode = JsonNode.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken))!;
        var todoNode = reportNode["todos"]!.AsArray()
            .Single(todo => todo!["id"]!.GetValue<string>() == "UWMIG015")!;
        todoNode["status"] = "accepted";
        await File.WriteAllTextAsync(
            reportPath,
            reportNode.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true }),
            TestContext.CancellationToken);

        var (firstExit, firstOutput) = await InvokeVerifyAsync(target);
        Assert.AreEqual(0, firstExit, firstOutput);
        using var firstReport = JsonDocument.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken));
        var firstTodo = firstReport.RootElement.GetProperty("todos")
            .EnumerateArray()
            .Single(todo => todo.GetProperty("id").GetString() == "UWMIG015");
        var firstGuidance = firstTodo.GetProperty("guidance").GetRawText();
        var firstLocations = firstTodo.GetProperty("locations").GetRawText();

        var (secondExit, secondOutput) = await InvokeVerifyAsync(target);
        Assert.AreEqual(0, secondExit, secondOutput);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(
            reportPath,
            TestContext.CancellationToken));
        var matchingTodos = report.RootElement.GetProperty("todos")
            .EnumerateArray()
            .Where(todo => todo.GetProperty("id").GetString() == "UWMIG015")
            .ToArray();
        Assert.HasCount(1, matchingTodos);
        Assert.AreEqual(
            "accepted",
            matchingTodos[0].GetProperty("status").GetString());
        Assert.AreEqual(
            firstGuidance,
            matchingTodos[0].GetProperty("guidance").GetRawText());
        Assert.AreEqual(
            firstLocations,
            matchingTodos[0].GetProperty("locations").GetRawText());
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
