using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using DeskNest.App.ViewModels;
using DeskNest.App.Views;
using DeskNest.Core.Workspace;

namespace DeskNest.App.Services;

// Opt-in developer probe. The PowerShell driver performs OS mouse drags in real Explorer;
// this side only verifies the resulting production events and confirms/undoes fixture imports.
internal static class ExplorerDragSmoke
{
    internal static async Task VerifyAsync(Window mainWindow, WorkspaceStore store)
    {
        var main = (MainWindowViewModel)mainWindow.DataContext!;
        var studio = main.Studio!;
        studio.OpenAddSpaceDialog();
        studio.NewSpaceName = "Explorer workspace";
        await studio.ConfirmAddSpaceCommand.ExecuteAsync(null);
        var space = store.Snapshot.Spaces.Single(s => s.Name == "Explorer workspace");
        if (Directory.Exists(space.Folder))
            throw new InvalidOperationException("Creating a managed space must not preempt its guarded import.");
        var area = mainWindow.Screens.ScreenFromWindow(mainWindow)!.WorkingArea;
        double scale = mainWindow.RenderScaling;
        int minimumWidth = OperatingSystem.IsMacOS() ? 1000 : 1100;
        if (area.Width / scale < minimumWidth || area.Height / scale < 600)
            throw new InvalidOperationException($"File-manager drag probe needs {minimumWidth}×600 logical pixels; no display settings will be changed.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var floating = new SpaceWindow(studio, space.Id, mainWindow)
        {
            Width = 400, Height = 460, Topmost = true, WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(area.X + (int)(16 * scale), area.Y + (int)(16 * scale))
        };
        floating.Show();
        var observedDrop = floating.FindControl<Border>("SpaceWindowDropSurface")!;
        foreach (var routed in new[] { Avalonia.Input.DragDrop.DragEnterEvent, Avalonia.Input.DragDrop.DropEvent })
            observedDrop.AddHandler(routed, (_, e) => Console.WriteLine("FILE_MANAGER_DRAG_EVENT:" +
                JsonSerializer.Serialize(new { Kind = e.RoutedEvent?.Name,
                    Formats = e.DataTransfer?.Formats.Select(format => format.Identifier).ToArray() })), handledEventsToo: true);
        try
        {
            foreach (bool directory in new[] { false, true })
            {
                string inbox = Directory.CreateDirectory(Path.Combine(store.DataDirectory, directory ? "Inbox-directory" : "Inbox-file")).FullName;
                string receiver = Directory.CreateDirectory(Path.Combine(store.DataDirectory, directory ? "Receiver-directory" : "Receiver-file")).FullName;
                string name = directory ? "Explorer project" : "Explorer document.txt";
                string source = Path.Combine(inbox, name);
                if (directory) Directory.CreateDirectory(Path.Combine(source, "empty"));
                string Content(string path) => directory ? Path.Combine(path, "document.txt") : path;
                await File.WriteAllTextAsync(Content(source), "Explorer drag fixture");
                mainWindow.WindowState = WindowState.Minimized;
                floating.Activate();
                await Task.Delay(250, deadline.Token);
                var drop = floating.FindControl<Border>("SpaceWindowDropSurface")!;
                var dropPoint = floating.PointToScreen(drop.TranslatePoint(new Point(drop.Bounds.Width / 2, drop.Bounds.Height / 2), floating)!.Value);
                var explorerRect = new { X = area.X + (int)(440 * scale), Y = area.Y + (int)(16 * scale), Width = area.Width - (int)(456 * scale), Height = (int)(540 * scale) };
                Announce(new { Stage = "inbound", Directory = directory, Folder = inbox, Item = name,
                    Point = dropPoint, Scale = scale, ExplorerRect = explorerRect, Window = floating.TryGetPlatformHandle()!.Handle.ToInt64() });
                await UntilAsync(() => studio.IsImportConfirmationOpen, deadline.Token, "Explorer drop did not reach import confirmation: " + studio.SpaceDropNotice);
                if (studio.ImportSourcePath != source || !File.Exists(Content(source)) || store.Snapshot.Files.Count != 0)
                    throw new InvalidOperationException("Explorer drop must request confirmation before moving or cataloging its external source.");
                await studio.ConfirmImportCommand.ExecuteAsync(null);
                var operation = store.Snapshot.Operations.Single(o => o.ImportSource?.Path == source);
                string published = Path.Combine(space.Folder, name);
                if (operation.Status != ProposedOperationStatus.Completed || File.Exists(Content(source)) || File.ReadAllText(Content(published)) != "Explorer drag fixture")
                    throw new InvalidOperationException("Confirmed Explorer import did not commit: " + studio.ImportError);

                var imported = studio.AllSpaces.Single(s => s.Id == space.Id).Files.Single(f => f.Id == operation.FileId);
                await studio.ExecuteRevealFileCommand.ExecuteAsync(imported);
                if (studio.FileActionNotice != studio.Localizer.GetString("Files.RevealSuccessNotice", name))
                    throw new InvalidOperationException("Open containing folder reported failure: " + studio.FileActionNotice);
                mainWindow.WindowState = WindowState.Minimized;
                floating.Activate();
                await Task.Delay(250, deadline.Token);
                var list = floating.FindControl<ListBox>("SpaceWindowFiles")!;
                var row = list.ContainerFromIndex(0) ?? throw new InvalidOperationException("No realized outgoing file row.");
                var rowPoint = floating.PointToScreen(row.TranslatePoint(new Point(70, row.Bounds.Height / 2), floating)!.Value);
                long revision = store.Snapshot.Revision;
                Announce(new { Stage = "outbound", Directory = directory, Folder = receiver, Item = name,
                    Point = rowPoint, Scale = scale, ExplorerRect = explorerRect, LocatedFolder = space.Folder,
                    Window = floating.TryGetPlatformHandle()!.Handle.ToInt64() });
                string received = Path.Combine(receiver, name);
                await UntilAsync(() => File.Exists(Content(received)), deadline.Token, "Explorer did not receive the outgoing file reference: " + studio.FileActionNotice);
                if (File.ReadAllText(Content(received)) != "Explorer drag fixture" || File.ReadAllText(Content(published)) != "Explorer drag fixture" ||
                    store.Snapshot.Revision != revision || directory && !System.IO.Directory.Exists(Path.Combine(received, "empty")))
                    throw new InvalidOperationException("Outgoing Explorer copy must preserve source, metadata, content and empty directories.");
                await studio.ExecuteUndoManualMoveCommand.ExecuteAsync(operation.Id);
                if (File.ReadAllText(Content(source)) != "Explorer drag fixture" || File.Exists(Content(published)) ||
                    store.Snapshot.Operations.Single(o => o.Id == operation.Id).Status != ProposedOperationStatus.Undone ||
                    directory && !System.IO.Directory.Exists(Path.Combine(source, "empty")))
                    throw new InvalidOperationException("Undo did not restore the external fixture after Explorer copied it.");
                Console.WriteLine($"EXPLORER_DRAG_CASE: {JsonSerializer.Serialize(new { Directory = directory, SpaceCreated = true, ImportConfirmed = true, ExternalCopyPreservedSource = true, UndoRestored = true })}");
            }
        }
        finally { floating.Close(); }
    }

    private static void Announce(object value)
    {
        Console.WriteLine("EXPLORER_DRAG_READY:" + JsonSerializer.Serialize(value));
        Console.Out.Flush();
    }

    private static async Task UntilAsync(Func<bool> predicate, CancellationToken token, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate())
        {
            token.ThrowIfCancellationRequested();
            if (elapsed.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(message);
            await Task.Delay(50, token);
        }
    }
}
