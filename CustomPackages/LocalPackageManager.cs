﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CustomBeatmaps.Util;
using UnityEngine;

using Path = Pri.LongPath.Path;
using Directory = Pri.LongPath.Directory;
using System.Linq;
using static Rhythm.BeatmapIndex;
using UnityEngine.SceneManagement;
using System.Security.Cryptography;
using CustomBeatmaps.Patches;

namespace CustomBeatmaps.CustomPackages
{
    /// <summary>
    /// Manages Local packages in the USER_PACKAGES or SERVER_PACKAGES directory
    /// </summary>
    public class LocalPackageManager
    {
        public Action<CustomLocalPackage> PackageUpdated;

        private readonly List<CustomLocalPackage> _packages = new List<CustomLocalPackage>();
        private readonly HashSet<string> _downloadedFolders = new HashSet<string>();

        private readonly Action<BeatmapException> _onLoadException;

        private readonly Queue<string> _loadQueue = new Queue<string>();

        private string _folder;
        private int _category;
        private FileSystemWatcher _watcher;

        public InitialLoadStateData InitialLoadState { get; private set; } = new InitialLoadStateData();
        public class InitialLoadStateData
        {
            public bool Loading;
            public int Loaded;
            public int Total;
        }

        public LocalPackageManager(Action<BeatmapException> onLoadException)
        {
            _onLoadException = onLoadException;
        }

        private void ReloadAll()
        {
            if (_folder == null)
                return;
            new Thread(() =>
            {
                Thread.CurrentThread.IsBackground = true;
                lock (_packages)
                {
                    InitialLoadState.Loading = true;
                    InitialLoadState.Loaded = 0;
                    InitialLoadState.Total = CustomPackageHelper.EstimatePackageCount(_folder);
                    ScheduleHelper.SafeLog($"RELOADING ALL PACKAGES FROM {_folder}");

                    _packages.Clear();
                    var packages = CustomPackageHelper.LoadLocalPackages(_folder, _category, loadedPackage =>
                    {
                        InitialLoadState.Loaded++;
                    }, _onLoadException);
                    ScheduleHelper.SafeLog($"(step 2)");
                    _packages.AddRange(packages);
                    lock (_downloadedFolders)
                    {
                        _downloadedFolders.Clear();
                        foreach (var package in _packages)
                        {
                            _downloadedFolders.Add(Path.GetFullPath(package.FolderName));
                        }
                    }
                    InitialLoadState.Loading = false;
                }
            }).Start();
        }

        private void UpdatePackage(string folderPath)
        {
            // Remove old package if there was one and update
            lock (_packages)
            {
                int toRemove = _packages.FindIndex(check => check.FolderName == folderPath);
                if (toRemove != -1)
                    _packages.RemoveAt(toRemove);
            }

            if (CustomPackageHelper.TryLoadLocalPackage(folderPath, _folder, out CustomLocalPackage package, _category, true,
                    _onLoadException))
            {
                ScheduleHelper.SafeInvoke(() => package.PkgSongs.ForEach(s => s.GetTexture()));
                ScheduleHelper.SafeLog($"UPDATING PACKAGE: {folderPath}");
                lock (_packages)
                {
                    // Remove old package if there was one and update
                    //int toRemove = _packages.FindIndex(check => check.FolderName == package.FolderName);
                    //if (toRemove != -1)
                    //    _packages.RemoveAt(toRemove);
                    _packages.Add(package);
                    lock (_downloadedFolders)
                    {
                        _downloadedFolders.Add(Path.GetFullPath(package.FolderName));
                    }
                }
                PackageUpdated?.Invoke(package);
            }
            else
            {
                ScheduleHelper.SafeLog($"CANNOT find package: {folderPath}");
            }

        }

        private void RemovePackage(string folderPath)
        {
            lock (_packages)
            {
                string fullPath = Path.GetFullPath(folderPath);
                int toRemove = _packages.FindIndex(check => check.FolderName == fullPath);
                if (toRemove != -1)
                {
                    var p = _packages[toRemove];
                    _packages.RemoveAt(toRemove);
                    lock (_downloadedFolders)
                    {
                        _downloadedFolders.Remove(fullPath);
                    }

                    ScheduleHelper.SafeLog($"REMOVED PACKAGE: {fullPath}");
                    PackageUpdated?.Invoke(p);
                }
                else
                {
                    ScheduleHelper.SafeLog($"CANNOT find package to remove: {folderPath}");
                }
            }
        }

        public List<CustomLocalPackage> Packages
        {
            get
            {
                if (InitialLoadState.Loading)
                {
                    return new List<CustomLocalPackage>();
                }
                lock (_packages)
                {
                    return _packages;
                }
            }
        }

        public List<CustomSongInfo> Songs
        {
            get
            {
                if (InitialLoadState.Loading)
                {
                    return new List<CustomSongInfo>();
                }
                lock (_packages)
                {
                    return _packages.SelectMany(p => p.PkgSongs).ToList();
                }
            }
        }

        public bool PackageExists(string folder)
        {
            lock (_downloadedFolders)
            {
                string targetFullPath = Path.GetFullPath(folder);
                return _downloadedFolders.Contains(targetFullPath);
            }
        }

        /// <summary>
        /// Given a server package URL and a beatmap info from the server, find our local beatmap.
        /// </summary>
        public (CustomLocalPackage, CustomBeatmapInfo) FindCustomBeatmapInfoFromServer(string serverPackageURL, string beatmapRelativeKeyPath)
        {
            beatmapRelativeKeyPath = beatmapRelativeKeyPath.Replace('/', '\\');

            string targetPackageFullPath = CustomPackageHelper.GetLocalFolderFromServerPackageURL(
                Config.Mod.ServerPackagesDir, serverPackageURL);
            targetPackageFullPath = Path.GetFullPath(targetPackageFullPath);

            bool foundPackage = false;
            foreach (var package in Packages)
            {
                string currentFullPath = Path.GetFullPath(package.FolderName);
                bool samePackage = currentFullPath == targetPackageFullPath;
                //ScheduleHelper.SafeLog($"{currentFullPath} compared to {targetPackageFullPath}");
                if (samePackage)
                {
                    foundPackage = true;
                    foreach (CustomBeatmapInfo cbinfo in package.PkgSongs.SelectMany(s => s.CustomBeatmaps))
                    {
                        string fullOSUPath = Path.GetFullPath(cbinfo.OsuPath);
                        string relativeOSUPath = fullOSUPath.Substring(targetPackageFullPath.Length + 1);
                        if (beatmapRelativeKeyPath == relativeOSUPath)
                        {
                            return (package, cbinfo);
                        }
                    }
                }
            }

            if (!foundPackage)
            {
                throw new InvalidOperationException($"Can't find package at {targetPackageFullPath}");
            }
            throw new InvalidOperationException(
                $"Can't find beatmap {beatmapRelativeKeyPath} at folder {targetPackageFullPath}");
        }

        public void SetFolder(string folder, int category)
        {
            if (folder == null)
                return;
            folder = Path.GetFullPath(folder);
            if (folder == _folder)
                return;
            
            _folder = folder;
            _category = category;

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            // Clear previous watcher
            if (_watcher != null)
            {
                _watcher.Dispose();
            }

            // Watch for changes
            _watcher = FileWatchHelper.WatchFolder(folder, true, OnFileChange);
            // Reload now
            ReloadAll();
        }

        private void OnFileChange(FileSystemEventArgs evt)
        {
            string changedFilePath = Path.GetFullPath(evt.FullPath);
            // The root folder within the packages folder we consider to be a "package"
            string basePackageFolder = Path.GetFullPath(Path.Combine(_folder, StupidMissingTypesHelper.GetPathRoot(changedFilePath.Substring(_folder.Length + 1))));

            // Special case: Root package folder is deleted, we delete a package.
            if (evt.ChangeType == WatcherChangeTypes.Deleted && basePackageFolder == changedFilePath)
            {
                ScheduleHelper.SafeLog($"Local Package DELETE: {basePackageFolder}");
                RemovePackage(basePackageFolder);
                return;
            }

            ScheduleHelper.SafeLog($"Local Package Change: {evt.ChangeType}: {basePackageFolder} ");

            lock (_loadQueue)
            {
                // We should refresh queued packages in bulk.
                bool isFirst = _loadQueue.Count == 0;
                if (!_loadQueue.Contains(basePackageFolder))
                {
                    //ScheduleHelper.SafeLog($"adding {basePackageFolder} to queue");
                    _loadQueue.Enqueue(basePackageFolder);
                }

                if (isFirst)
                {
                    // Wait for potential other loads to come in
                    Task.Run(async () =>
                    {
                        await Task.Delay(400);
                        RefreshQueuedPackages();
                        
                    }).ContinueWith(task => ScheduleHelper.SafeInvoke(() => {
                        _loadQueue.Clear();
                        ArcadeHelper.ReloadArcadeList();
                    }));
                    
                }
            }
            
        }

        private void RefreshQueuedPackages()
        {
            while (true)
            {
                lock (_loadQueue)
                {
                    if (_loadQueue.Count <= 0)
                        break;
                    UpdatePackage(_loadQueue.Dequeue());
                }
            }
        }
    }
}
