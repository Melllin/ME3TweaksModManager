﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MassEffectModManagerCore.GameDirectories;
using MassEffectModManagerCore.gamefileformats.sfar;
using MassEffectModManagerCore.modmanager.helpers;
using MassEffectModManagerCore.modmanager.me3tweaks;
using MassEffectModManagerCore.modmanager.objects;
using MassEffectModManagerCore.ui;
using Microsoft.AppCenter.Analytics;
using Microsoft.VisualBasic;
using Serilog;
using SevenZip;

namespace MassEffectModManagerCore.modmanager.usercontrols
{
    /// <summary>
    /// Interaction logic for ModInstaller.xaml
    /// </summary>
    public partial class ModInstaller : MMBusyPanelBase
    {
        public ObservableCollectionExtended<object> AlternateOptions { get; } = new ObservableCollectionExtended<object>();

        public bool InstallationSucceeded { get; private set; }
        public static readonly int PERCENT_REFRESH_COOLDOWN = 125;
        public bool ModIsInstalling { get; set; }
        public bool AllOptionsAreAutomatic { get; private set; }
        public ModInstaller(Mod modBeingInstalled, GameTarget gameTarget)
        {
            DataContext = this;
            lastPercentUpdateTime = DateTime.Now;
            this.ModBeingInstalled = modBeingInstalled;
            this.gameTarget = gameTarget;
            Action = $"Preparing to install";
            InitializeComponent();
        }


        public Mod ModBeingInstalled { get; }
        private GameTarget gameTarget;
        private DateTime lastPercentUpdateTime;
        public bool InstallationCancelled;

        public enum ModInstallCompletedStatus
        {
            INSTALL_SUCCESSFUL,
            USER_CANCELED_INSTALLATION,
            INSTALL_FAILED_USER_CANCELED_MISSING_MODULES,
            INSTALL_FAILED_ALOT_BLOCKING,
            INSTALL_FAILED_REQUIRED_DLC_MISSING,
            INSTALL_WRONG_NUMBER_OF_COMPLETED_ITEMS,
            NO_RESULT_CODE
        }



        public string Action { get; set; }
        public int Percent { get; set; }
        public Visibility PercentVisibility { get; set; } = Visibility.Collapsed;

        private void BeginInstallingMod()
        {
            ModIsInstalling = true;
            Log.Information($"BeginInstallingMod(): {ModBeingInstalled.ModName}");
            NamedBackgroundWorker bw = new NamedBackgroundWorker($"ModInstaller-{ModBeingInstalled.ModName}");
            bw.WorkerReportsProgress = true;
            bw.DoWork += InstallModBackgroundThread;
            bw.RunWorkerCompleted += ModInstallationCompleted;
            //bw.ProgressChanged += ModProgressChanged;
            bw.RunWorkerAsync();
        }


        private void InstallModBackgroundThread(object sender, DoWorkEventArgs e)
        {
            Log.Information($"Mod Installer Background thread starting");
            var installationJobs = ModBeingInstalled.InstallationJobs;
            var gamePath = gameTarget.TargetPath;
            var gameDLCPath = MEDirectories.DLCPath(gameTarget);

            Directory.CreateDirectory(gameDLCPath); //me1/me2 missing dlc might not have this folder

            //Check we can install
            var missingRequiredDLC = ModBeingInstalled.ValidateRequiredModulesAreInstalled(gameTarget);
            if (missingRequiredDLC.Count > 0)
            {
                e.Result = (ModInstallCompletedStatus.INSTALL_FAILED_REQUIRED_DLC_MISSING, missingRequiredDLC);
                return;
            }


            //Check/warn on official headers
            if (!PrecheckHeaders(gameDLCPath, installationJobs))
            {
                e.Result = ModInstallCompletedStatus.INSTALL_FAILED_USER_CANCELED_MISSING_MODULES;
                return;
            }

            //todo: If statment on this
            Utilities.InstallBinkBypass(gameTarget); //Always install binkw32, don't bother checking if it is already ASI version.

            //Prepare queues
            (Dictionary<ModJob, (Dictionary<string, string> fileMapping, List<string> dlcFoldersBeingInstalled)> unpackedJobMappings,
                List<(ModJob job, string sfarPath, Dictionary<string, string> sfarInstallationMapping)> sfarJobs) installationQueues =
                ModBeingInstalled.GetInstallationQueues(gameTarget);

            if (gameTarget.ALOTInstalled)
            {
                //Check if any packages are being installed. If there are, we will block this installation.
                bool installsPackageFile = false;
                foreach (var jobMappings in installationQueues.unpackedJobMappings)
                {
                    installsPackageFile |= jobMappings.Value.fileMapping.Keys.Any(x => x.EndsWith(".pcc", StringComparison.InvariantCultureIgnoreCase));
                    installsPackageFile |= jobMappings.Value.fileMapping.Keys.Any(x => x.EndsWith(".u", StringComparison.InvariantCultureIgnoreCase));
                    installsPackageFile |= jobMappings.Value.fileMapping.Keys.Any(x => x.EndsWith(".upk", StringComparison.InvariantCultureIgnoreCase));
                    installsPackageFile |= jobMappings.Value.fileMapping.Keys.Any(x => x.EndsWith(".sfm", StringComparison.InvariantCultureIgnoreCase));
                }

                foreach (var jobMappings in installationQueues.sfarJobs)
                {
                    installsPackageFile |= jobMappings.sfarInstallationMapping.Keys.Any(x => x.EndsWith(".pcc", StringComparison.InvariantCultureIgnoreCase));
                    installsPackageFile |= jobMappings.sfarInstallationMapping.Keys.Any(x => x.EndsWith(".u", StringComparison.InvariantCultureIgnoreCase));
                    installsPackageFile |= jobMappings.sfarInstallationMapping.Keys.Any(x => x.EndsWith(".upk", StringComparison.InvariantCultureIgnoreCase));
                    installsPackageFile |= jobMappings.sfarInstallationMapping.Keys.Any(x => x.EndsWith(".sfm", StringComparison.InvariantCultureIgnoreCase));
                }

                if (installsPackageFile)
                {
                    if (Settings.DeveloperMode)
                    {
                        Log.Warning("ALOT is installed and user is attemping to install a mod (in developer mode). Prompting user to cancel installation");

                        bool cancel = false;
                        Application.Current.Dispatcher.Invoke(delegate
                        {
                            var res = Xceed.Wpf.Toolkit.MessageBox.Show(Window.GetWindow(this), $"ALOT is installed and this mod installs package files. Continuing to install this mod will likely cause broken textures to occur or game crashes due to invalid texture pointers and possibly empty mips. It will also put your ALOT installation into an unsupported configuration.\n\nContinue to install {ModBeingInstalled.ModName}? You have been warned.", $"Broken textures warning", MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.No);
                            cancel = res == MessageBoxResult.No;
                        });
                        if (cancel)
                        {
                            e.Result = ModInstallCompletedStatus.USER_CANCELED_INSTALLATION;
                            return;
                        }
                        Log.Warning("User installing mod anyways even with ALOT installed");
                    }
                    else
                    {
                        Log.Error("ALOT is installed. Installing mods that install package files after installing ALOT is not permitted.");
                        //ALOT Installed, this is attempting to install a package file
                        e.Result = ModInstallCompletedStatus.INSTALL_FAILED_ALOT_BLOCKING;
                        return;
                    }
                }
            }
            Action = $"Installing";
            PercentVisibility = Visibility.Visible;
            Percent = 0;

            int numdone = 0;

            //Calculate number of installation tasks beforehand
            int numFilesToInstall = installationQueues.unpackedJobMappings.Select(x => x.Value.fileMapping.Count).Sum();
            numFilesToInstall += installationQueues.sfarJobs.Select(x => x.sfarInstallationMapping.Count).Sum() * (ModBeingInstalled.IsInArchive ? 2 : 1); //*2 as we have to extract and install
            Debug.WriteLine("Number of expected installation tasks: " + numFilesToInstall);
            void FileInstalledCallback(string target)
            {
                numdone++;
                Debug.WriteLine("Installed: " + target);
                Action = "Installing";
                var now = DateTime.Now;
                if (numdone > numFilesToInstall) Debug.WriteLine($"Percentage calculated is wrong. Done: {numdone} NumToDoTotal: {numFilesToInstall}");
                if ((now - lastPercentUpdateTime).Milliseconds > PERCENT_REFRESH_COOLDOWN)
                {
                    //Don't update UI too often. Once per second is enough.
                    Percent = (int)(numdone * 100.0 / numFilesToInstall);
                    lastPercentUpdateTime = now;
                }
            }

            //Stage: Unpacked files build map
            Dictionary<string, string> fullPathMappingDisk = new Dictionary<string, string>();
            Dictionary<int, string> fullPathMappingArchive = new Dictionary<int, string>();
            SortedSet<string> customDLCsBeingInstalled = new SortedSet<string>();
            foreach (var unpackedQueue in installationQueues.unpackedJobMappings)
            {
                foreach (var originalMapping in unpackedQueue.Value.fileMapping)
                {
                    //always unpacked
                    //if (unpackedQueue.Key == ModJob.JobHeader.CUSTOMDLC || unpackedQueue.Key == ModJob.JobHeader.BALANCE_CHANGES || unpackedQueue.Key == ModJob.JobHeader.BASEGAME)
                    //{

                    string sourceFile;
                    if (unpackedQueue.Key.JobDirectory == null)
                    {
                        sourceFile = FilesystemInterposer.PathCombine(ModBeingInstalled.IsInArchive, ModBeingInstalled.ModPath, originalMapping.Value);
                    }
                    else
                    {
                        sourceFile = FilesystemInterposer.PathCombine(ModBeingInstalled.IsInArchive, ModBeingInstalled.ModPath, unpackedQueue.Key.JobDirectory, originalMapping.Value);
                    }



                    if (unpackedQueue.Key.Header == ModJob.JobHeader.ME1_CONFIG)
                    {

                        var destFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BioWare", "Mass Effect", "Config", originalMapping.Key);
                        if (ModBeingInstalled.IsInArchive)
                        {
                            int archiveIndex = ModBeingInstalled.Archive.ArchiveFileNames.IndexOf(sourceFile, StringComparer.InvariantCultureIgnoreCase);
                            fullPathMappingArchive[archiveIndex] = destFile; //used for extraction indexing
                            if (archiveIndex == -1)
                            {
                                Log.Error("Archive Index is -1 for file " + sourceFile + ". This will probably throw an exception!");
                                Debugger.Break();
                            }
                            fullPathMappingDisk[sourceFile] = destFile; //used for redirection
                        }
                        else
                        {
                            fullPathMappingDisk[sourceFile] = destFile;
                        }
                    }
                    else
                    {

                        var destFile = Path.Combine(unpackedQueue.Key.Header == ModJob.JobHeader.CUSTOMDLC ? MEDirectories.DLCPath(gameTarget) : gameTarget.TargetPath, originalMapping.Key); //official

                        //Extract Custom DLC name
                        if (unpackedQueue.Key.Header == ModJob.JobHeader.CUSTOMDLC)
                        {
                            var custDLC = destFile.Substring(gameDLCPath.Length, destFile.Length - gameDLCPath.Length).TrimStart('\\', '/');
                            var nextSlashIndex = custDLC.IndexOf('\\');
                            if (nextSlashIndex == -1) nextSlashIndex = custDLC.IndexOf('/');
                            if (nextSlashIndex != -1)
                            {
                                custDLC = custDLC.Substring(0, nextSlashIndex);
                                customDLCsBeingInstalled.Add(custDLC);
                            }
                        }

                        if (ModBeingInstalled.IsInArchive)
                        {
                            int archiveIndex = ModBeingInstalled.Archive.ArchiveFileNames.IndexOf(sourceFile, StringComparer.InvariantCultureIgnoreCase);
                            fullPathMappingArchive[archiveIndex] = destFile; //used for extraction indexing
                            if (archiveIndex == -1)
                            {
                                Log.Error("Archive Index is -1 for file " + sourceFile + ". This will probably throw an exception!");
                                Debugger.Break();
                            }
                            fullPathMappingDisk[sourceFile] = destFile; //used for redirection
                        }
                        else
                        {
                            fullPathMappingDisk[sourceFile] = destFile;
                        }
                    }

                    //}
                }
            }

            //Substage: Add SFAR staging targets
            string sfarStagingDirectory = (ModBeingInstalled.IsInArchive && installationQueues.sfarJobs.Count > 0) ? Directory.CreateDirectory(Path.Combine(Utilities.GetTempPath(), "SFARJobStaging")).FullName : null; //don't make directory if we don't need one
            if (sfarStagingDirectory != null)
            {
                foreach (var sfarJob in installationQueues.sfarJobs)
                {
                    foreach (var fileToInstall in sfarJob.sfarInstallationMapping)
                    {
                        string sourceFile = FilesystemInterposer.PathCombine(ModBeingInstalled.IsInArchive, ModBeingInstalled.ModPath, sfarJob.job.JobDirectory, fileToInstall.Value);
                        int archiveIndex = ModBeingInstalled.Archive.ArchiveFileNames.IndexOf(sourceFile, StringComparer.InvariantCultureIgnoreCase);
                        if (archiveIndex == -1)
                        {
                            Log.Error("Archive Index is -1 for file " + sourceFile + ". This will probably throw an exception!");
                            Debugger.Break();
                        }
                        string destFile = Path.Combine(sfarStagingDirectory, sfarJob.job.JobDirectory, fileToInstall.Value);
                        fullPathMappingArchive[archiveIndex] = destFile; //used for extraction indexing
                        fullPathMappingDisk[sourceFile] = destFile; //used for redirection
                        Debug.WriteLine($"SFAR Disk Staging: {fileToInstall.Key} => {destFile}");
                    }
                }
            }

            foreach (var cdbi in customDLCsBeingInstalled)
            {
                var path = Path.Combine(gameDLCPath, cdbi);
                if (Directory.Exists(path))
                {
                    Log.Information("Deleting existing DLC directory: " + path);
                    Utilities.DeleteFilesAndFoldersRecursively(path);
                }
            }

            //Stage: Unpacked files installation
            if (!ModBeingInstalled.IsInArchive)
            {
                //Direct copy
                Log.Information($"Installing {fullPathMappingDisk.Count} unpacked files into game directory");
                CopyDir.CopyFiles_ProgressBar(fullPathMappingDisk, FileInstalledCallback);
            }
            else
            {
                Action = "Loading mod archive";
                //Extraction to destination
                string installationRedirectCallback(ArchiveFileInfo info)
                {
                    var inArchivePath = info.FileName;
                    var redirectedPath = fullPathMappingDisk[inArchivePath];
                    Debug.WriteLine($"Redirecting {inArchivePath} to {redirectedPath}");
                    return redirectedPath;
                }

                ModBeingInstalled.Archive.FileExtractionStarted += (sender, args) =>
                {
                    //CLog.Information("Extracting mod file for installation: " + args.FileInfo.FileName, Settings.LogModInstallation);
                };
                List<string> filesInstalled = new List<string>();
                List<string> filesToInstall = installationQueues.unpackedJobMappings.SelectMany(x => x.Value.fileMapping.Keys).ToList();
                ModBeingInstalled.Archive.FileExtractionFinished += (sender, args) =>
                {
                    if (args.FileInfo.IsDirectory) return; //ignore
                    if (!fullPathMappingArchive.ContainsKey(args.FileInfo.Index)) return; //archive extracted this file (in memory) but did not do anything with this file (7z)
                    FileInstalledCallback(args.FileInfo.FileName);
                    filesInstalled.Add(args.FileInfo.FileName);
                    //Debug.WriteLine($"{args.FileInfo.FileName} as file { numdone}");
                    //Debug.WriteLine(numdone);
                };
                ModBeingInstalled.Archive.ExtractFiles(gameTarget.TargetPath, installationRedirectCallback, fullPathMappingArchive.Keys.ToArray()); //directory parameter shouldn't be used here as we will be redirecting everything
                //filesInstalled.Sort();
                //filesToInstall.Sort();
                //Debug.WriteLine("Files installed:");
                //foreach (var f in filesInstalled)
                //{
                //    Debug.WriteLine(f);
                //}
                //Debug.WriteLine("Files expected:");
                //foreach (var f in filesToInstall)
                //{
                //    Debug.WriteLine(f);
                //}

            }

            //Write MetaCMM
            List<string> addedDLCFolders = new List<string>();
            foreach (var v in installationQueues.unpackedJobMappings)
            {
                addedDLCFolders.AddRange(v.Value.dlcFoldersBeingInstalled);
            }
            foreach (var addedDLCFolder in addedDLCFolders)
            {
                var metacmm = Path.Combine(addedDLCFolder, "_metacmm.txt");
                ModBeingInstalled.HumanReadableCustomDLCNames.TryGetValue(Path.GetFileName(addedDLCFolder), out var assignedDLCName);
                string contents = $"{assignedDLCName ?? ModBeingInstalled.ModName}\n{ModBeingInstalled.ModVersionString}\n{App.BuildNumber}\n{Guid.NewGuid().ToString()}";
                File.WriteAllText(metacmm, contents);
            }

            //Stage: SFAR Installation
            foreach (var sfarJob in installationQueues.sfarJobs)
            {
                InstallIntoSFAR(sfarJob, ModBeingInstalled, FileInstalledCallback, ModBeingInstalled.IsInArchive ? sfarStagingDirectory : null);
            }


            //Main installation step has completed
            CLog.Information("Main stage of mod installation has completed", Settings.LogModInstallation);
            Percent = (int)(numdone * 100.0 / numFilesToInstall);

            //Remove outdated custom DLC
            foreach (var outdatedDLCFolder in ModBeingInstalled.OutdatedCustomDLC)
            {
                var outdatedDLCInGame = Path.Combine(gameDLCPath, outdatedDLCFolder);
                if (Directory.Exists(outdatedDLCInGame))
                {
                    Log.Information("Deleting outdated custom DLC folder: " + outdatedDLCInGame);
                    Utilities.DeleteFilesAndFoldersRecursively(outdatedDLCInGame);
                }
            }

            //Install supporting ASI files if necessary
            //Todo: Upgrade to version detection code from ME3EXP to prevent conflicts

            Action = "Installing support files";
            CLog.Information("Installing supporting ASI files", Settings.LogModInstallation);
            if (ModBeingInstalled.Game == Mod.MEGame.ME1)
            {
                Utilities.InstallEmbeddedASI("ME1-DLC-ModEnabler-v1.0", 1.0, gameTarget);
            }
            else if (ModBeingInstalled.Game == Mod.MEGame.ME2)
            {
                //None right now
            }
            else
            {
                //Todo: Port detection code from ME3Exp
                //Utilities.InstallEmbeddedASI("ME3Logger_truncating-v1.0", 1.0, gameTarget);
                if (ModBeingInstalled.GetJob(ModJob.JobHeader.BALANCE_CHANGES) != null)
                {
                    Utilities.InstallEmbeddedASI("BalanceChangesReplacer-v2.0", 2.0, gameTarget);
                }
            }

            if (sfarStagingDirectory != null)
            {
                Utilities.DeleteFilesAndFoldersRecursively(Utilities.GetTempPath());
            }

            if (numFilesToInstall == numdone)
            {
                e.Result = ModInstallCompletedStatus.INSTALL_SUCCESSFUL;
                Action = "Installed";
            }
            else
            {
                Log.Warning($"Number of completed items does not equal the amount of items to install! Number installed {numdone} Number expected: {numFilesToInstall}");
                e.Result = ModInstallCompletedStatus.INSTALL_WRONG_NUMBER_OF_COMPLETED_ITEMS;
            }
        }

        private bool InstallIntoSFAR((ModJob job, string sfarPath, Dictionary<string, string> fileMapping) sfarJob, Mod mod, Action<string> FileInstalledCallback = null, string ForcedSourcePath = null)
        {

            int numfiles = sfarJob.fileMapping.Count;
            //Todo: Check all newfiles exist
            //foreach (string str in diskFiles)
            //{
            //    if (!File.Exists(str))
            //    {
            //        Console.WriteLine("Source file on disk doesn't exist: " + str);
            //        EndProgram(1);
            //    }
            //}

            //Open SFAR
            Log.Information($"Installing {sfarJob.fileMapping.Count} files into {sfarJob.sfarPath}");
            DLCPackage dlc = new DLCPackage(sfarJob.sfarPath);

            //Add or Replace file install
            foreach (var entry in sfarJob.fileMapping)
            {
                string entryPath = entry.Key.Replace('\\', '/');
                if (!entryPath.StartsWith('/')) entryPath = '/' + entryPath; //Ensure path starts with /
                int index = dlc.FindFileEntry(entryPath);
                //Todo: For archive to immediate installation we will need to modify this ModPath value to point to some temporary directory
                //where we have extracted files destined for SFAR files as we cannot unpack solid archives to streams.
                var sourcePath = Path.Combine(ForcedSourcePath ?? mod.ModPath, sfarJob.job.JobDirectory, entry.Value);
                if (index >= 0)
                {
                    dlc.ReplaceEntry(sourcePath, index);
                    CLog.Information("Replaced file within SFAR: " + entry.Key, Settings.LogModInstallation);
                }
                else
                {
                    dlc.AddFileQuick(sourcePath, entryPath);
                    CLog.Information("Added new file to SFAR: " + entry.Key, Settings.LogModInstallation);
                }
                FileInstalledCallback?.Invoke(entryPath);
            }

            //Todo: Support deleting files from sfar (I am unsure if this is actually ever used and I might remove this feature)
            //if (options.DeleteFiles)
            //{
            //    DLCPackage dlc = new DLCPackage(options.SFARPath);
            //    List<int> indexesToDelete = new List<int>();
            //    foreach (string file in options.Files)
            //    {
            //        int idx = dlc.FindFileEntry(file);
            //        if (idx == -1)
            //        {
            //            if (options.IgnoreDeletionErrors)
            //            {
            //                continue;
            //            }
            //            else
            //            {
            //                Console.WriteLine("File doesn't exist in archive: " + file);
            //                EndProgram(1);
            //            }
            //        }
            //        else
            //        {
            //            indexesToDelete.Add(idx);
            //        }
            //    }
            //    if (indexesToDelete.Count > 0)
            //    {
            //        dlc.DeleteEntries(indexesToDelete);
            //    }
            //    else
            //    {
            //        Console.WriteLine("No files were found in the archive that matched the input list for --files.");
            //    }
            //}

            return true;
        }

        /// <summary>
        /// Checks if DLC specified by the job installation headers exist and prompt user to continue or not if the DLC is not found. This is only used jobs that are not CUSTOMDLC.'
        /// </summary>
        /// <param name="gameDLCPath">Game DLC path</param>
        /// <param name="installationJobs">List of jobs to look through and validate</param>
        /// <returns></returns>
        private bool PrecheckHeaders(string gameDLCPath, List<ModJob> installationJobs)
        {
            //if (ModBeingInstalled.Game != Mod.MEGame.ME3) { return true; } //me1/me2 don't have dlc header checks like me3
            foreach (var job in installationJobs)
            {
                if (job.Header == ModJob.JobHeader.ME1_CONFIG)
                {
                    //Make sure config files exist.
                    var destFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BioWare", "Mass Effect", "Config", "BIOEngine.ini");
                    if (!File.Exists(destFile))
                    {
                        bool cancel = false;

                        Application.Current.Dispatcher.Invoke(new Action(() =>
                        {

                            Xceed.Wpf.Toolkit.MessageBox.Show(Window.GetWindow(this), "Mods that modify configuration files require that Mass Effect be run at least once before they are installed. This is to ensure the game does not overwrite the files on first boot.", "Game must be run at least once", MessageBoxButton.OK, MessageBoxImage.Error);
                            cancel = true;
                            return;
                        }));
                        if (cancel) return false;
                    }

                    continue;
                }

                if (!MEDirectories.IsOfficialDLCInstalled(job.Header, gameTarget))
                {
                    Log.Warning($"DLC not installed that mod is marked to modify: {job.Header}, prompting user.");
                    //Prompt user
                    bool cancel = false;
                    Application.Current.Dispatcher.Invoke(new Action(() =>
                    {
                        var dlcName = ModJob.GetHeadersToDLCNamesMap(ModBeingInstalled.Game)[job.Header];
                        string resolvedName = dlcName;
                        MEDirectories.OfficialDLCNames(ModBeingInstalled.Game).TryGetValue(dlcName, out resolvedName);
                        string message = $"{ModBeingInstalled.ModName} installs files into {dlcName} ({resolvedName}) DLC, which is not installed.";
                        if (job.RequirementText != null)
                        {
                            message += "\n\nThe mod lists the following reason for this task:";
                            message += $"\n{job.RequirementText}";
                        }

                        message += "\n\nThe mod might not function properly without this DLC installed first. Continue installing anyways?";
                        MessageBoxResult result = Xceed.Wpf.Toolkit.MessageBox.Show(message, $"{MEDirectories.OfficialDLCNames(ModBeingInstalled.Game)[ModJob.GetHeadersToDLCNamesMap(ModBeingInstalled.Game)[job.Header]]} DLC not installed", MessageBoxButton.YesNo, MessageBoxImage.Error);
                        if (result == MessageBoxResult.No)
                        {
                            cancel = true;
                            return;
                        }

                    }));
                    if (cancel)
                    {
                        Log.Error("User canceling installation");

                        return false;
                    }

                    Log.Warning($"User continuing installation anyways");
                }
                else
                {
                    CLog.Information("Official headers check passed for header " + job.Header, Settings.LogModInstallation);
                }
            }

            return true;
        }

        private void ModInstallationCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                Log.Error("An error occured during mod installation.\n" + App.FlattenException(e.Error));
            }

            var telemetryResult = ModInstallCompletedStatus.NO_RESULT_CODE;
            if (e.Result is ModInstallCompletedStatus mcis)
            {
                telemetryResult = mcis;
                //Success, canceled (generic and handled), ALOT canceled
                InstallationSucceeded = mcis == ModInstallCompletedStatus.INSTALL_SUCCESSFUL;
                if (mcis == ModInstallCompletedStatus.INSTALL_FAILED_ALOT_BLOCKING)
                {
                    InstallationCancelled = true;
                    Xceed.Wpf.Toolkit.MessageBox.Show($"Installation of mods that install package files to targets that have ALOT installed is not allowed. Package files must be installed before ALOT.", $"Installation blocked", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else if (mcis == ModInstallCompletedStatus.INSTALL_WRONG_NUMBER_OF_COMPLETED_ITEMS)
                {
                    Xceed.Wpf.Toolkit.MessageBox.Show($"Mod was installed but did not pass installation count verification. This is likely a bug in Mod Manger, please report this to Mgamerz on Discord.", $"Installation succeeded, maybe", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else if (mcis == ModInstallCompletedStatus.INSTALL_FAILED_USER_CANCELED_MISSING_MODULES)
                {
                    InstallationCancelled = true;
                }
            }
            else if (e.Result is (ModInstallCompletedStatus result, List<string> items))
            {
                telemetryResult = result;
                //Failures with results
                Log.Warning("Installation failed with status " + result.ToString());
                switch (result)
                {
                    case ModInstallCompletedStatus.INSTALL_FAILED_REQUIRED_DLC_MISSING:
                        string dlcText = "";
                        foreach (var dlc in items)
                        {
                            var info = ThirdPartyServices.GetThirdPartyModInfo(dlc, ModBeingInstalled.Game);
                            if (info != null)
                            {
                                dlcText += $"\n - {info.modname} ({dlc})";
                            }
                            else
                            {
                                dlcText += $"\n - {dlc}";
                            }
                        }
                        InstallationCancelled = true;
                        Xceed.Wpf.Toolkit.MessageBox.Show($"This mod requires the following DLC/mods to be installed prior to installation:{dlcText}", $"Required content missing", MessageBoxButton.OK, MessageBoxImage.Error);
                        break;
                }
            }
            else
            {
                throw new Exception("Mod installer did not have return code. This should be caught and handled, but it wasn't.");
            }
            Analytics.TrackEvent("Installed a mod", new Dictionary<string, string>()
            {
                { "Mod name", $"{ModBeingInstalled.ModName} {ModBeingInstalled.ModVersionString}" },
                { "Installed from", ModBeingInstalled.IsInArchive ? "Archive" : "Library" },
                { "Game", ModBeingInstalled.Game.ToString() },
                { "Result", telemetryResult.ToString() }
            });
            OnClosing(DataEventArgs.Empty);
        }

        private void InstallStart_Click(object sender, RoutedEventArgs e)
        {
            BeginInstallingMod();
        }

        private void InstallCancel_Click(object sender, RoutedEventArgs e)
        {
            InstallationSucceeded = false;
            InstallationCancelled = true;
            OnClosing(DataEventArgs.Empty);
        }

        public override void HandleKeyPress(object sender, KeyEventArgs e)
        {
            //throw new NotImplementedException();
        }

        private void AlternateItem_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is DockPanel dp)
            {
                if (dp.DataContext is AlternateDLC ad && ad.IsManual)
                {
                    ad.IsSelected = !ad.IsSelected;
                }
                else if (dp.DataContext is AlternateFile af && af.IsManual)
                {
                    af.IsSelected = !af.IsSelected;
                }
            }
        }

        public override void OnPanelVisible()
        {
            //Write check
            //Write check
            var canWrite = Utilities.IsDirectoryWritable(gameTarget.TargetPath);
            if (!canWrite)
            {
                //needs write permissions
                InstallationCancelled = true;
                Xceed.Wpf.Toolkit.MessageBox.Show($"Your user account does not have write privileges to the game directory. You can grant your account privileges using the \"Grant write privleges to game directories\" option in the tools menu.", $"Cannot write to game directory", MessageBoxButton.OK, MessageBoxImage.Warning);
                OnClosing(DataEventArgs.Empty);
                return;
            }

            //Detect incompatible DLC
            var dlcMods = VanillaDatabaseService.GetInstalledDLCMods(gameTarget);
            if (ModBeingInstalled.IncompatibleDLC.Count > 0)
            {
                //Check for incompatible DLC.
                List<string> incompatibleDLC = new List<string>();
                foreach (var incompat in ModBeingInstalled.IncompatibleDLC)
                {
                    if (dlcMods.Contains(incompat, StringComparer.InvariantCultureIgnoreCase))
                    {
                        var tpmi = ThirdPartyServices.GetThirdPartyModInfo(incompat, ModBeingInstalled.Game);
                        if (tpmi != null)
                        {
                            incompatibleDLC.Add($" - {incompat} ({tpmi.modname})");
                        }
                        else
                        {
                            incompatibleDLC.Add(" - " + incompat);

                        }
                    }
                }

                if (incompatibleDLC.Count > 0)
                {
                    string message = $"The following DLC mods are listed as incompatible with {ModBeingInstalled.ModName}:\n";
                    message += string.Join('\n', incompatibleDLC);
                    message += $"\n\nIn order to install {ModBeingInstalled.ModName}, the above listed Custom DLC mods must be removed first.\nYou can manage installed Custom DLC by clicking Target Info button.";
                    InstallationCancelled = true;
                    Xceed.Wpf.Toolkit.MessageBox.Show(message, $"Incompatible DLC detected", MessageBoxButton.OK, MessageBoxImage.Error);
                    OnClosing(DataEventArgs.Empty);
                    return;
                }
            }

            //Detect outdated DLC
            if (ModBeingInstalled.OutdatedCustomDLC.Count > 0)
            {
                //Check for incompatible DLC.
                List<string> outdatedDLC = new List<string>();
                foreach (var outdatedItem in ModBeingInstalled.OutdatedCustomDLC)
                {
                    if (dlcMods.Contains(outdatedItem, StringComparer.InvariantCultureIgnoreCase))
                    {
                        var tpmi = ThirdPartyServices.GetThirdPartyModInfo(outdatedItem, ModBeingInstalled.Game);
                        if (tpmi != null)
                        {
                            outdatedDLC.Add($" - {outdatedItem} ({tpmi.modname})");
                        }
                        else
                        {
                            outdatedDLC.Add(" - " + outdatedItem);

                        }
                    }
                }

                if (outdatedDLC.Count > 0)
                {
                    string message = $"The following DLC mods are listed as outdated/no longer relevant by {ModBeingInstalled.ModName} and will be removed upon installation:\n";
                    message += string.Join('\n', outdatedDLC);
                    message += $"\n\nClick yes to continue installing this {ModBeingInstalled.ModName}, or click no to abort installation.";
                    InstallationCancelled = true;
                    var result = Xceed.Wpf.Toolkit.MessageBox.Show(message, $"Outdated DLC detected", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result == MessageBoxResult.No)
                    {
                        InstallationCancelled = true;
                        OnClosing(DataEventArgs.Empty);
                        return;
                    }
                }
            }

            //See if any alternate options are available and display them even if they are all autos
            AllOptionsAreAutomatic = true;
            foreach (var job in ModBeingInstalled.InstallationJobs)
            {
                AlternateOptions.AddRange(job.AlternateDLCs);
                AlternateOptions.AddRange(job.AlternateFiles);
            }

            foreach (object o in AlternateOptions)
            {
                if (o is AlternateDLC altdlc)
                {
                    altdlc.SetupInitialSelection(gameTarget);
                    if (altdlc.IsManual) AllOptionsAreAutomatic = false;
                }
                else if (o is AlternateFile altfile)
                {
                    altfile.SetupInitialSelection(gameTarget);
                    if (altfile.IsManual) AllOptionsAreAutomatic = false;
                }
            }

            if (AlternateOptions.Count == 0)
            {
                //Just start installing mod
                BeginInstallingMod();
            }
        }
    }
}
