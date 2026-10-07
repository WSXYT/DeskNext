using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskNest.App.ViewModels;
using DeskNest.App.Views;
using DeskNest.Core.Workspace;

namespace DeskNest.App.Services;

// Opt-in headless lifecycle check. Deferred callbacks below test task ownership, not model quality.
internal static class OnboardingModelSmoke
{
    internal static async Task VerifyJevSessionAsync(string root)
    {
        await using var store = await WorkspaceStore.OpenAsync(root);
        await store.UpdateAsync(s => s with
        {
            OnboardingStep = 2,
            Settings = s.Settings with { Provider = InferenceProvider.Jev, ManagedRoot = Path.Combine(root, "Spaces") }
        });
        await using var owner = new MainWindowViewModel(store, ownsStore: false);
        var window = new MainWindow(owner);
        window.Show();
        try
        {
            var wizard = owner.Onboarding!;
            var setup = wizard.ModelDeployment!;
            var view = window.GetVisualDescendants().OfType<OnboardingView>().Single();
            var input = view.FindControl<TextBox>("OnboardingJevKeyInput")!;
            var consent = view.FindControl<CheckBox>("OnboardingJevConsent")!;
            var check = view.FindControl<Button>("OnboardingCheckJevButton")!;
            Dispatcher.UIThread.RunJobs();
            if (!input.IsEffectivelyVisible || input.PasswordChar == default || input.FlowDirection != Avalonia.Media.FlowDirection.LeftToRight ||
                check.Command != setup.CheckJevConnectionCommand || setup.JevSendConsent || setup.JevSessionKey.Length != 0 ||
                view.FindControl<Button>("OnboardingClearJevButton")!.Command != setup.ClearJevSessionCommand)
                throw new InvalidOperationException("Onboarding Jev must use masked session state with no inherited permission.");
            consent.IsChecked = true; // Missing key must refuse before HTTP.
            await setup.CheckJevConnectionCommand.ExecuteAsync(null);
            if (setup.JevConnectionNotice != setup.Localizer["Classification.JevSetup"])
                throw new InvalidOperationException("Missing onboarding key was not refused.");
            const string key = "onboarding-fixture-not-a-real-key";
            input.Text = key;
            if (setup.JevSendConsent) throw new InvalidOperationException("Key entry did not revoke permission; refuse to send the fixture.");
            await setup.CheckJevConnectionCommand.ExecuteAsync(null); // Unapproved: must return before HTTP.
            if (setup.JevConnectionNotice != setup.Localizer["Classification.JevSetup"])
                throw new InvalidOperationException("Entering an onboarding key must revoke send permission.");
            consent.IsChecked = true; // Do not invoke the connection command after this.
            await wizard.GoNextAsync();
            await wizard.GoNextAsync();
            await wizard.GoNextAsync();
            await wizard.CompleteOnboardingAsync();
            if (!ReferenceEquals(owner.Studio, setup) || setup.JevSessionKey != key || !setup.JevSendConsent ||
                store.Snapshot.Settings.Provider != InferenceProvider.Jev ||
                File.ReadAllText(Path.Combine(root, "workspace.json")).Contains(key, StringComparison.Ordinal) ||
                File.ReadAllText(Path.Combine(root, "workspace.json.bak")).Contains(key, StringComparison.Ordinal))
                throw new InvalidOperationException("Completing onboarding must carry session state in memory only.");
            await setup.ResetOnboardingAsync();
            if (owner.Onboarding!.ModelDeployment!.JevSendConsent || setup.JevSessionKey.Length != 0)
                throw new InvalidOperationException("Restarting onboarding must clear the prior cloud permission.");
            setup.JevSessionKey = key;
            setup.JevSendConsent = true;
            await owner.DisposeAsync();
            if (setup.JevSendConsent || setup.JevSessionKey.Length != 0)
                throw new InvalidOperationException("Disposal must clear keys and permission while onboarding too.");
            Console.WriteLine("OOBE_JEV_SESSION_VERIFIED: permission/refusal/transfer/disposal only; no live API request.");
        }
        finally { window.Close(); }
    }

    internal static async Task<bool> VerifyAsync(string root)
    {
        await using var store = await WorkspaceStore.OpenAsync(root);
        await store.UpdateAsync(s => s with
        {
            OnboardingStep = 2,
            Settings = s.Settings with { ManagedRoot = Path.Combine(root, "Spaces") }
        });
        await using var owner = new MainWindowViewModel(store, ownsStore: false);
        var window = new MainWindow(owner);
        window.Show();
        try
        {
            var wizard = owner.Onboarding ?? throw new InvalidOperationException("Missing onboarding.");
            var deployment = wizard.ModelDeployment ?? throw new InvalidOperationException("Missing onboarding deployment.");
            var view = window.GetVisualDescendants().OfType<OnboardingView>().Single();
            Dispatcher.UIThread.RunJobs();
            var download = view.FindControl<Button>("OnboardingDownloadModelButton")!;
            var cancel = view.FindControl<Button>("OnboardingCancelModelButton")!;
            var location = view.FindControl<TextBox>("OnboardingModelInstallRoot")!;
            if (!download.IsEffectivelyVisible || download.Command != deployment.InstallLocalModelPackageCommand ||
                cancel.Command != deployment.InstallLocalModelPackageCancelCommand || !location.IsReadOnly ||
                deployment.ModelInstallRoot != Path.Combine(root, "models") || deployment.OnInstallLocalModelPackage is null ||
                Directory.Exists(deployment.ModelInstallRoot) || store.Snapshot.Settings.ModelCacheDirectory is not null)
                throw new InvalidOperationException("Onboarding must expose the existing installer without starting it or inventing an active model.");

            // Exercise the actual installer refusal, with no HTTP request and no model activation.
            string invalid = Path.Combine(root, "invalid.zip");
            await File.WriteAllTextAsync(invalid, "not a model package");
            long revision = store.Snapshot.Revision;
            await deployment.InstallLocalModelPackageCommand.ExecuteAsync(invalid);
            if (store.Snapshot.Revision != revision || wizard.ModelCacheDirectory.Length != 0 ||
                string.IsNullOrEmpty(deployment.ModelInstallNotice) || File.ReadAllText(invalid) != "not a model package")
                throw new InvalidOperationException("Refused onboarding installation changed model selection or input.");

            // Optional real ZIP -> verification -> App-hosted CPU loading, without a download.
            string? archive = Environment.GetEnvironmentVariable("DESKNEXT_TEST_OOBE_ARCHIVE");
            bool realPackage = !string.IsNullOrEmpty(archive);
            if (realPackage)
            {
                await deployment.InstallLocalModelPackageCommand.ExecuteAsync(archive);
                if (!File.Exists(Path.Combine(deployment.SettingsModelCache, "manifest.json")) ||
                    wizard.ModelCacheDirectory.Length != 0 || deployment.ModelInstallProgress != 100 ||
                    store.Snapshot.Revision != revision || store.Snapshot.Settings.ModelCacheDirectory is not null)
                    throw new InvalidOperationException("Onboarding real package did not produce a verified, unsaved draft: " + deployment.ModelInstallNotice);
                Console.WriteLine("OOBE_MODEL_PACKAGE_INSTALLED_VERIFIED: true");
            }
            string priorModel = wizard.ModelCacheDirectory;

            var blocked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            deployment.OnInstallLocalModelPackage = (_, _, token) => blocked.Task.WaitAsync(token);
            Task cancelled = deployment.InstallLocalModelPackageCommand.ExecuteAsync(null);
            wizard.SelectedProvider = InferenceProvider.Jev;
            await cancelled;
            if (deployment.InstallLocalModelPackageCommand.IsRunning || wizard.ModelCacheDirectory != priorModel ||
                store.Snapshot.Revision != revision)
                throw new InvalidOperationException("Changing the onboarding provider must cancel, not activate a late model.");
            wizard.SelectedProvider = InferenceProvider.Laya;

            int calls = 0;
            var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            deployment.OnInstallLocalModelPackage = (_, progress, token) =>
            {
                calls++;
                progress.Report(37);
                return finish.Task.WaitAsync(token);
            };
            Task running = deployment.InstallLocalModelPackageCommand.ExecuteAsync(null);
            await wizard.GoNextAsync();
            await wizard.GoNextAsync();
            await wizard.GoNextAsync();
            await wizard.CompleteOnboardingAsync();
            Dispatcher.UIThread.RunJobs();
            if (running.IsCompleted || calls != 1 || !owner.IsStudioActive || owner.Onboarding is not null ||
                !ReferenceEquals(owner.Studio, deployment) || !deployment.InstallLocalModelPackageCommand.IsRunning ||
                deployment.SettingsManagedRoot != store.Snapshot.Settings.ManagedRoot)
                throw new InvalidOperationException("Entering the workbench must neither block on nor restart its deployment task.");
            string? persisted = store.Snapshot.Settings.ModelCacheDirectory;
            string prepared = Path.Combine(root, "lifecycle-result-not-a-real-model");
            finish.SetResult(prepared);
            await running;
            if (deployment.SettingsModelCache != prepared || deployment.ModelInstallProgress != 100 ||
                store.Snapshot.Settings.ModelCacheDirectory != persisted || wizard.ModelCacheDirectory != priorModel)
                throw new InvalidOperationException("A late deployment must only update the workbench draft and detach the wizard.");
            await deployment.SaveSettingsAsync();
            if (store.Snapshot.Settings.ModelCacheDirectory != prepared || store.Snapshot.Operations.Count != 0)
                throw new InvalidOperationException("The deployment draft needs explicit settings activation, without file operations.");

            await deployment.ResetOnboardingAsync();
            var closing = owner.Onboarding!.ModelDeployment!;
            var never = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            closing.OnInstallLocalModelPackage = (_, _, token) => never.Task.WaitAsync(token);
            Task closingTask = closing.InstallLocalModelPackageCommand.ExecuteAsync(null);
            revision = store.Snapshot.Revision;
            await owner.DisposeAsync();
            await closingTask;
            if (closing.InstallLocalModelPackageCommand.IsRunning || store.Snapshot.Revision != revision ||
                store.Snapshot.Settings.ModelCacheDirectory != prepared)
                throw new InvalidOperationException("Closing the owner must cancel onboarding deployment without saving a result.");
            Console.WriteLine("OOBE_MODEL_DEPLOYMENT_LIFECYCLE_VERIFIED: task transfer, cancellation, explicit activation; no cloud calls.");
            return realPackage;
        }
        finally { window.Close(); }
    }
}
