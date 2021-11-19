﻿using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Samples;
using pylorak.Windows;

namespace pylorak.TinyWall
{

    internal class Updater
    {
        private enum UpdaterState
        {
            GettingDescriptor,
            DescriptorReady,
            DownloadingUpdate,
            UpdateDownloadReady
        }

        private TaskDialog TDialog;
        private UpdaterState State;
        private UpdateDescriptor Descriptor;
        private string ErrorMsg;
        private volatile int DownloadProgress;

        internal static void StartUpdate()
        {
            Updater updater = new Updater();
            updater.State = UpdaterState.GettingDescriptor;

            updater.TDialog = new TaskDialog();
            updater.TDialog.CustomMainIcon = Resources.Icons.firewall;
            updater.TDialog.WindowTitle = Resources.Messages.TinyWall;
            updater.TDialog.MainInstruction = Resources.Messages.TinyWallUpdater;
            updater.TDialog.Content = Resources.Messages.PleaseWaitWhileTinyWallChecksForUpdates;
            updater.TDialog.AllowDialogCancellation = false;
            updater.TDialog.CommonButtons = TaskDialogCommonButtons.Cancel;
            updater.TDialog.ShowMarqueeProgressBar = true;
            updater.TDialog.Callback = updater.DownloadTickCallback;
            updater.TDialog.CallbackData = updater;
            updater.TDialog.CallbackTimer = true;

            Thread UpdateThread = new Thread((ThreadStart)delegate()
                {
                    try
                    {
                        updater.Descriptor = UpdateChecker.GetDescriptor();
                        updater.State = UpdaterState.DescriptorReady;
                    }
                    catch
                    {
                        updater.ErrorMsg = Resources.Messages.ErrorCheckingForUpdates;
                    }
                });
            UpdateThread.Start();

            switch (updater.TDialog.Show())
            {
                case (int)DialogResult.Cancel:
                    UpdateThread.Interrupt();
                    if (!UpdateThread.Join(500))
                        UpdateThread.Abort();
                    break;
                case (int)DialogResult.OK:
                    updater.CheckVersion();
                    break;
                case (int)DialogResult.Abort:
                    Utils.ShowMessageBox(updater.ErrorMsg, Resources.Messages.TinyWall, TaskDialogCommonButtons.Ok, TaskDialogIcon.Error);
                    break;
            }
        }

        private void CheckVersion()
        {
            UpdateModule UpdateModule = UpdateChecker.GetMainAppModule(this.Descriptor);
            Version oldVersion = new Version(System.Windows.Forms.Application.ProductVersion);
            Version newVersion = new Version(UpdateModule.ComponentVersion);

            bool win10v1903 = VersionInfo.Win10OrNewer && (Environment.OSVersion.Version.Build >= 18362);
            bool WindowsNew_AnyTwUpdate = win10v1903 && (newVersion > oldVersion);
            bool WindowsOld_TwMinorFixOnly = (newVersion > oldVersion) && (newVersion.Major == oldVersion.Major) && (newVersion.Minor == oldVersion.Minor);

            if (WindowsNew_AnyTwUpdate || WindowsOld_TwMinorFixOnly)
            {
                string prompt = string.Format(CultureInfo.CurrentCulture, Resources.Messages.UpdateAvailable, UpdateModule.ComponentVersion);
                if (Utils.ShowMessageBox(prompt, Resources.Messages.TinyWallUpdater, TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, TaskDialogIcon.Warning) == DialogResult.Yes)
                    DownloadUpdate(UpdateModule);
            }
            else
            {
                string prompt = Resources.Messages.NoUpdateAvailable;
                Utils.ShowMessageBox(prompt, Resources.Messages.TinyWallUpdater, TaskDialogCommonButtons.Ok, TaskDialogIcon.Information);
            }
        }

        private void DownloadUpdate(UpdateModule mainModule)
        {
            ErrorMsg = null;
            Updater updater = this;
            updater.TDialog = new TaskDialog();
            updater.TDialog.CustomMainIcon = Resources.Icons.firewall;
            updater.TDialog.WindowTitle = Resources.Messages.TinyWall;
            updater.TDialog.MainInstruction = Resources.Messages.TinyWallUpdater;
            updater.TDialog.Content = Resources.Messages.DownloadingUpdate;
            updater.TDialog.AllowDialogCancellation = false;
            updater.TDialog.CommonButtons = TaskDialogCommonButtons.Cancel;
            updater.TDialog.ShowProgressBar = true;
            updater.TDialog.Callback = updater.DownloadTickCallback;
            updater.TDialog.CallbackData = updater;
            updater.TDialog.CallbackTimer = true;
            updater.TDialog.EnableHyperlinks = true;

            State = UpdaterState.DownloadingUpdate;

            string tmpFile = Path.GetTempFileName() + ".msi";
            Uri UpdateURL = new Uri(mainModule.UpdateURL);
            using (WebClient HTTPClient = new WebClient())
            {
                HTTPClient.DownloadFileCompleted += new AsyncCompletedEventHandler(Updater_DownloadFinished);
                HTTPClient.DownloadProgressChanged += new DownloadProgressChangedEventHandler(Updater_DownloadProgressChanged);
                HTTPClient.DownloadFileAsync(UpdateURL, tmpFile, tmpFile);

                switch (updater.TDialog.Show())
                {
                    case (int)DialogResult.Cancel:
                        HTTPClient.CancelAsync();
                        break;
                    case (int)DialogResult.OK:
                        InstallUpdate(tmpFile);
                        break;
                    case (int)DialogResult.Abort:
                        Utils.ShowMessageBox(updater.ErrorMsg, Resources.Messages.TinyWall, TaskDialogCommonButtons.Ok, TaskDialogIcon.Error);
                        break;
                }
            }
        }

        private static void InstallUpdate(string localFilePath)
        {
            Utils.StartProcess(localFilePath, null, false, false);
        }

        private void Updater_DownloadFinished(object sender, AsyncCompletedEventArgs e)
        {
            if (e.Cancelled || (e.Error != null))
            {
                ErrorMsg = Resources.Messages.DownloadInterrupted;
                return;
            }

            State = UpdaterState.UpdateDownloadReady;
        }

        private void Updater_DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            DownloadProgress = e.ProgressPercentage;
        }

        private bool DownloadTickCallback(ActiveTaskDialog taskDialog, TaskDialogNotificationArgs args, object callbackData)
        {
            Updater updater = callbackData as Updater;

            switch (args.Notification)
            {
                case TaskDialogNotification.Created:
                    if (updater.State == UpdaterState.GettingDescriptor)
                        taskDialog.SetProgressBarMarquee(true, 25);
                    break;
                case TaskDialogNotification.Timer:
                    if (!string.IsNullOrEmpty(updater.ErrorMsg))
                        taskDialog.ClickButton((int)DialogResult.Abort);
                    switch (updater.State)
                    {
                        case UpdaterState.DescriptorReady:
                        case UpdaterState.UpdateDownloadReady:
                            taskDialog.ClickButton((int)DialogResult.OK);
                            break;
                        case UpdaterState.DownloadingUpdate:
                        taskDialog.SetProgressBarPosition(updater.DownloadProgress);
                            break;
                    }
                    break;
            }
            return false;
        }
    }

    internal static class UpdateChecker
    {
        private const int UPDATER_VERSION = 5;
        private const string URL_UPDATE_DESCRIPTOR = @"https://tinywall.pados.hu/updates/UpdVer{0}/update.xml";

        internal static UpdateDescriptor GetDescriptor()
        {
            string url = string.Format(CultureInfo.InvariantCulture, URL_UPDATE_DESCRIPTOR, UPDATER_VERSION);
            string tmpFile = Path.GetTempFileName();

            try
            {
                using (WebClient HTTPClient = new WebClient())
                {
                    HTTPClient.Headers.Add("TW-Version", Application.ProductVersion);
                    HTTPClient.DownloadFile(url, tmpFile);
                }

                UpdateDescriptor descriptor = SerializationHelper.LoadFromXMLFile<UpdateDescriptor>(tmpFile);
                if (descriptor.MagicWord != "TinyWall Update Descriptor")
                    throw new ApplicationException("Bad update descriptor file.");

                return descriptor;
            }
            finally
            {
                File.Delete(tmpFile);
            }
        }

        internal static UpdateModule GetMainAppModule(UpdateDescriptor descriptor)
        {
            for (int i = 0; i < descriptor.Modules.Length; ++i)
            {
                if (descriptor.Modules[i].Component.Equals("TinyWall"))
                    return descriptor.Modules[i];
            }

            return null;
        }
        internal static UpdateModule GetHostsFileModule(UpdateDescriptor descriptor)
        {
            for (int i = 0; i < descriptor.Modules.Length; ++i)
            {
                if (descriptor.Modules[i].Component.Equals("HostsFile"))
                    return descriptor.Modules[i];
            }

            return null;
        }
        internal static UpdateModule GetDatabaseFileModule(UpdateDescriptor descriptor)
        {
            for (int i = 0; i < descriptor.Modules.Length; ++i)
            {
                if (descriptor.Modules[i].Component.Equals("Database"))
                    return descriptor.Modules[i];
            }

            return null;
        }
    }
}
