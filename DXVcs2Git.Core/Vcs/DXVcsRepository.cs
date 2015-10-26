﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using DXVCS;
using DXVCSClient;

namespace DXVcs2Git.DXVcs {
    class DXVcsRepository : IDXVcsRepository {
        readonly string serviceUrl;
        readonly DXVcsServiceProvider serviceProvider;
        readonly FileSystem fileSystem;

        IDXVCSService Service {
            get { return serviceProvider.GetService(serviceUrl); }
        }

        internal DXVcsRepository(string serviceUrl) {
            this.serviceProvider = new DXVcsServiceProvider();
            this.serviceUrl = serviceUrl;
            fileSystem = new FileSystem();

            ValidateService();
        }
        void ValidateService() {
            bool isAdmin;
            var service = Service;
            if (service == null)
                throw new ArgumentNullException("service");
            if (!service.IsCorrectUser(out isAdmin))
                throw new ApplicationException("Invalid user name");
        }

        public FileVersionInfo[] GetFileHistory(string vcsFile, out string fileName) {
            if (string.IsNullOrEmpty(vcsFile))
                throw new ArgumentException("vcsFile");

            fileName = Path.GetFileName(vcsFile);

            var fileHistory = new FileHistory(vcsFile, Service);
            var result = new List<FileVersionInfo>(fileHistory.Count);
            result.AddRange(fileHistory);
            result.Reverse();
            return result.ToArray();
        }

        public FileDiffInfo GetFileDiffInfo(string vcsFile, SpacesAction spacesAction = SpacesAction.IgnoreAll) {
            return GetFileDiffInfo(vcsFile, null, spacesAction);
        }

        public FileDiffInfo GetFileDiffInfo(string vcsFile, Action<int, int> progressAction, SpacesAction spacesAction) {
            var history = new FileHistory(vcsFile, Service);
            FileDiffInfo diffInfo = new FileDiffInfo(history.Count);
            diffInfo.SpacesAction = spacesAction;

            int index = 0;
            foreach (FileVersionInfo fileVersionInfo in history) {
                if (progressAction != null)
                    progressAction(index, history.Count);

                diffInfo.AddItem(index, fileVersionInfo);
                index++;
            }
            return diffInfo;
        }
        public IList<ProjectHistoryInfo> GetProjectHistory(string vcsPath, bool recursive, DateTime? from = null, DateTime? to = null) {
            if (string.IsNullOrEmpty(vcsPath))
                throw new ArgumentException("vcsPath");

            int maxState;
            string id = Service.GetProjectHistoryAsync(vcsPath, recursive, out maxState);
            var request = PrepareProjectHistoryRequest(from ?? DateTime.MinValue, to ?? DateTime.Now, string.Empty, string.Empty, HistoryItems.AllItems, true);
            List<ProjectHistoryInfo> infos = new List<ProjectHistoryInfo>();
            Service.GetProjectHistoryRequest(id, request);
            int state;
            while (true) {
                ProjectHistoryInfo[] info;
                bool last = Service.GetProjectHistoryNext(id, out info, out state);
                if (info != null) {
                    infos.AddRange(info);
                }
                if (!last)
                    break;
            }
            return infos;
        }
        ProjectHistoryRequest PrepareProjectHistoryRequest(DateTime from, DateTime to, string findUser, string findComment, HistoryItems whatItems, bool showFileHistory) {
            ProjectHistoryRequest request = new ProjectHistoryRequest();
            request.FindUser = string.IsNullOrEmpty(findUser) ? null : findUser;
            request.FindComment = string.IsNullOrEmpty(findComment) ? null : findComment;
            request.HideProjectHistory = whatItems == HistoryItems.Labels;
            request.ShowFileHistory = !request.HideProjectHistory && showFileHistory;
            request.ShowLabels = whatItems != HistoryItems.WithoutLabels;
            request.From = from;
            request.To = to;
            return request;
        }
        public enum HistoryItems {
            AllItems = 0x00,
            Labels = 0x01,
            WithoutLabels = 0x02
        };
        public void GetLatestFileVersion(string vcsFile, string fileName) {
            if (string.IsNullOrEmpty(vcsFile))
                throw new ArgumentException("vcsFile");

            if (string.IsNullOrEmpty(fileName))
                throw new ArgumentException("path");

            byte[] data = DXVCSHelpers.TryToDecompressData(Service.GetFileData(vcsFile, null));
            File.WriteAllBytes(fileName, data);
        }

        public void Get(string vcsFile, string fileName, int version) {
            if (string.IsNullOrEmpty(vcsFile))
                throw new ArgumentException("vcsFile");

            if (string.IsNullOrEmpty(fileName))
                throw new ArgumentException("path");

            if (version <= 0)
                throw new ArgumentException("version");

            byte[] data = DXVCSHelpers.TryToDecompressData(Service.GetFileData(vcsFile, version));
            File.WriteAllBytes(fileName, data);
        }
        public void GetProject(string vcsPath, string localPath, DateTime timeStamp) {
            if (string.IsNullOrEmpty(vcsPath))
                throw new ArgumentException("vcsFile");
            GetLatestVersion(vcsPath, localPath, true, timeStamp, true, true, ReplaceWriteable.Replace, FileTime.Current, new VcsClientBatchState());
        }

        public enum FileTime {
            Default, Current, Modification, CheckIn
        }
        public enum ReplaceWriteable {
            Default, Ask, Replace, Skip
        } //, Merge TODO
        class BlockInfo {
            public int MagicPos;
            public int BlockIndex;
            public int[] Paths;
            public FileStateInfo2[] Info;
            public bool Last;
        }
        public enum FileBaseInfoState {
            Exists = 0x00,
            Missing = 0x01,
            Modified = 0x02,
            Outdated = 0x03,
            NeedMerge = 0x04,
            Locked = 0x05,
            Error = 0x06
        }

        public bool GetLatestVersion(string vcsPath, string localProjectPath, bool buildTree, object option, bool recursive, bool makeWritable, ReplaceWriteable replaceWriteableState, FileTime fileTimeState, VcsClientBatchState batchState) {
            CleanUpDirectory(localProjectPath);

            Queue<Exception> exceptionQueue = new Queue<Exception>();
            vcsPath = HelperPaths.RemoveFinishSlash(vcsPath);
            localProjectPath = HelperPaths.RemoveFinishSlash(localProjectPath);
            AutoResetEvent getDataEvent = new AutoResetEvent(true);
            AutoResetEvent decompressEvent = new AutoResetEvent(true);
            AutoResetEvent saveEvent = new AutoResetEvent(true);
            AutoResetEvent getBlockEvent = new AutoResetEvent(false);
            int magicCount;
            string id = Service.Get(Environment.MachineName, vcsPath, option, recursive, out magicCount);
            AccessDeniedInfo[] accessList = Service.TakeAccessInfo(id);
            if (accessList != null) {
                string path = vcsPath.TrimEnd('/');
                foreach (AccessDeniedInfo accessInfo in accessList) {
                    string message = string.Format("Resources.NoGetProjectPermissions", DateTime.Now.ToString(), path, accessInfo.ObjectName);
                }
            }
            Dictionary<string, string> localPathDict = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
            bool cancel = false;
            int lastMagic = 0;
            BlockInfo blockInfo = new BlockInfo();
            QueueBlock(id, getBlockEvent, blockInfo, exceptionQueue);
            List<string> pathList = new List<string>();
            try {
                while (true) {
                    getBlockEvent.WaitOne();
                    ProcessException(exceptionQueue);
                    if (!blockInfo.Last)
                        break;
                    int magicPos = blockInfo.MagicPos;
                    int blockIndex = blockInfo.BlockIndex;
                    int[] paths = blockInfo.Paths;
                    FileStateInfo2[] info = blockInfo.Info;
                    QueueBlock(id, getBlockEvent, blockInfo, exceptionQueue);
                    if (paths != null && blockIndex >= 0) {
                        bool[] restData = new bool[paths.Length];
                        string[] localPath = new string[paths.Length];
                        string[] path = new string[paths.Length];
                        DateTime[] fileTime = new DateTime[paths.Length];
                        bool[] setNormal = new bool[paths.Length];
                        bool[] fileExists = new bool[paths.Length];
                        bool[] doCheckOut = new bool[paths.Length];
                        bool doGetData = false;
                        bool wasCheckOut = false;
                        double step = ((double)magicPos - lastMagic) / paths.Length;
                        string curProject = string.Empty;
                        DateTime curProjectDate = DateTime.MinValue;
                        for (int i = 0; i < paths.Length; i++) {
                            if (info[i].IsNull) {
                                string curPostProject = HelperPaths.RemoveFinishSlash(info[i].Name);
                                pathList.Add(curPostProject);
                                if (!string.IsNullOrEmpty(curPostProject)) {
                                    curProject = vcsPath + "/" + curPostProject;
                                }
                                else {
                                    curProject = vcsPath;
                                }
                                string curLocalFolder;
                                if (buildTree) {
                                    curLocalFolder = localProjectPath + @"\" + curPostProject.Replace('/', '\\');
                                }
                                else {
                                    curLocalFolder = info[i].CheckOutFolder; //Hack ;)
                                }
                                curLocalFolder = HelperPaths.RemoveFinishSlash(curLocalFolder);
                                CreateDirectory(curLocalFolder);
                                localPathDict.Add(curProject, curLocalFolder);
                            }
                            else {
                                string curPostProject = pathList[paths[i]];
                                string nextProject = HelperPaths.Combine(vcsPath, curPostProject);
                                if (nextProject != curProject) {
                                    curProject = nextProject;
                                    curProjectDate = DateTime.MinValue;
                                }
                                string curLocalFolder;
                                if (localPathDict.TryGetValue(curProject, out curLocalFolder)) {
                                    string curFileName = info[i].Name;
                                    string curLocalPath = curLocalFolder + @"\" + curFileName;
                                    string curPath = curProject + "/" + curFileName;
                                    try {
                                        FileAttributes fa;
                                        DateTime fileModification;
                                        if (fileSystem.GetAttributes(curLocalPath, out fa, out fileModification)) {
                                            fileExists[i] = true;
                                            FileBaseInfoState fileState;
                                            bool needWrite = GetLocalFileModified(info[i], curLocalPath, curPath, curProjectDate, out fileState, fileModification);
                                            if (!needWrite && fileState == FileBaseInfoState.Locked) {
                                                continue;
                                            }
                                            if ((fa & FileAttributes.ReadOnly) == FileAttributes.ReadOnly) {
                                                if (needWrite) {
                                                    doGetData = true;
                                                    restData[i] = true;
                                                    localPath[i] = curLocalPath;
                                                    path[i] = curPath;
                                                    fileTime[i] = SelectFileTime(info[i], fileTimeState);
                                                }
                                                continue;
                                            }
                                            if ((makeWritable) && !needWrite)
                                                continue;
                                            if (batchState.ActionToExistFile.Action == ActionWithCopy.Replace) {
                                                doGetData = true;
                                                restData[i] = needWrite;
                                                localPath[i] = curLocalPath;
                                                path[i] = curPath;
                                                fileTime[i] = SelectFileTime(info[i], fileTimeState);
                                                continue;
                                            }
                                            if (batchState.ActionToExistFile.Action == ActionWithCopy.Leave) { continue; }
                                            if (batchState.ActionToExistFile.Action == ActionWithCopy.CheckOut) {
                                                localPath[i] = curLocalPath;
                                                path[i] = curPath;
                                                doCheckOut[i] = true;
                                                wasCheckOut = true;
                                                setNormal[i] = true;
                                            }
                                        }
                                        else
                                            if (fileSystem.DirectoryExists(curLocalFolder)) {
                                            doGetData = true;
                                            restData[i] = true;
                                            localPath[i] = curLocalPath;
                                            path[i] = curPath;
                                            bool differentHost;
                                            setNormal[i] = (makeWritable || info[i].CheckedOut) && info[i].CheckedOutMe && EqualsCheckOutParams(info[i], curLocalPath, out differentHost);
                                            fileTime[i] = SelectFileTime(info[i], fileTimeState);
                                        }
                                    }
                                    finally {
                                        if (makeWritable) {
                                            doGetData = true;
                                            localPath[i] = curLocalPath;
                                            setNormal[i] = true;
                                        }
                                    }
                                }
                            }
                        }
                        if (doGetData) {
                            Service.GetBlockDataReq(id, blockIndex, restData);
                            getDataEvent.WaitOne();
                            getDataEvent.Set();
                            ProcessException(exceptionQueue);
                            getDataEvent.Reset();
                            QueueAll(id, blockIndex, getDataEvent, decompressEvent, saveEvent, localPath, path, fileTime, doCheckOut, setNormal, fileExists, exceptionQueue, step);
                        }
                        else {
                            getDataEvent.WaitOne();
                            ProcessException(exceptionQueue);
                            getDataEvent.Set();
                        }
                        if (wasCheckOut) {
                            List<string> pathToCheckOut = new List<string>();
                            List<string> localPathToCheckOut = new List<string>();
                            List<string> commentToCheckOut = new List<string>();
                            for (int i = 0; i < path.Length; i++) {
                                if (doCheckOut[i]) {
                                    pathToCheckOut.Add(path[i]);
                                    localPathToCheckOut.Add(HelperPaths.GetDirectory(localPath[i]));
                                    commentToCheckOut.Add(string.Empty);
                                }
                            }
                            string checkOutId = Service.CheckOut(System.Environment.MachineName, pathToCheckOut.ToArray(), localPathToCheckOut.ToArray(), commentToCheckOut.ToArray(), null);
                            Dictionary<int, AccessDeniedInfo> accessDict = CreateAccessDict(Service.TakeAccessInfo(checkOutId));
                            for (int i = 0; i < localPathToCheckOut.Count; i++) {
                                string local = HelperPaths.Combine(localPathToCheckOut[i], HelperPaths.GetFile(pathToCheckOut[i]));
                                if (accessDict == null || !accessDict.ContainsKey(i)) {
                                    fileSystem.SetAttributes(local, FileAttributes.Normal);
                                }
                                else {
                                    string message = string.Format("Resources.NoCheckOutFilePermissions", DateTime.Now, pathToCheckOut[i]);
                                }
                            }
                        }
                        lastMagic = magicPos;
                    }
                }
            }
            finally {
                getDataEvent.WaitOne();
                decompressEvent.WaitOne();
                saveEvent.WaitOne();
                if (!cancel)
                    Service.ConfirmGetEnd(id);
            }
            return true;
        }
        void CleanUpDirectory(string localProjectPath) {
            CreateDirectory(localProjectPath);
            foreach (var file in Directory.EnumerateFiles(localProjectPath))
                File.Delete(file);
            foreach (var dir in Directory.EnumerateDirectories(localProjectPath)) {
                string dirName = Path.GetFileName(dir);
                if (dirName == ".git")
                    continue;
                Directory.Delete(dir, true);
            }
        }
        static Dictionary<int, AccessDeniedInfo> CreateAccessDict(AccessDeniedInfo[] accessList) {
            if (accessList == null)
                return null;
            Dictionary<int, AccessDeniedInfo> accessDict = new Dictionary<int, AccessDeniedInfo>();
            foreach (AccessDeniedInfo accessInfo in accessList) {
                if (accessInfo.Data != null && accessInfo.Data is int) {
                    accessDict.Add((int)accessInfo.Data, accessInfo);
                }
            }
            return accessDict;
        }

        void QueueAll(string id, int blockIndex, AutoResetEvent getDataEvent, AutoResetEvent decompressEvent, AutoResetEvent saveEvent,
                            string[] localPath, string[] path, DateTime[] fileTime, bool[] doCheckOut, bool[] setNormal, bool[] fileExists, Queue<Exception> exceptionQueue, double step) {

            byte[][] data;
            try {
                try {
                    do {
                        data = Service.GetBlockData(id, blockIndex, false);
                    } while (data == null);
                    decompressEvent.WaitOne();
                }
                finally {
                    getDataEvent.Set();
                }
                try {
                    for (int i = 0; i < data.Length; i++) {
                        if (data[i] == null) { continue; }
                        data[i] = DXVCSHelpers.TryToDecompressData(data[i]);
                    }
                    saveEvent.WaitOne();
                }
                finally {
                    decompressEvent.Set();
                }
                try {
                    for (int i = 0; i < data.Length; i++) {
                        if (data[i] == null || localPath[i] == null || path[i] == null || doCheckOut[i]) {
                            if (localPath[i] != null && fileSystem.Exists(localPath[i])) {
                                if (setNormal[i])
                                    fileSystem.SetAttributes(localPath[i], FileAttributes.Normal);
                                else
                                    fileSystem.SetAttributes(localPath[i], FileAttributes.ReadOnly);
                            }
                            continue;
                        }
                        GetLatestReplaceFile(new FileDataLocation(path[i], localPath[i], data[i]), fileTime[i], fileExists[i], null);
                        if ((setNormal[i]) && localPath[i] != null && fileSystem.Exists(localPath[i]))
                            fileSystem.SetAttributes(localPath[i], FileAttributes.Normal);
                    }
                }
                finally {
                    saveEvent.Set();
                }
            }
            catch (DXVCSGetNextTimeoutException) { }
            catch (Exception ex) {
                lock (exceptionQueue) {
                    exceptionQueue.Enqueue(ex);
                }
            }
        }
        public void GetLatestReplaceFile(FileDataLocation fileLocation, DateTime fileTime, bool? exists, bool? checkedOut) {
            if (exists == null)
                exists = fileSystem.Exists(fileLocation.LocalPath);
            fileSystem.WriteAllBytes(fileLocation.LocalPath, fileLocation.Data);
            if (fileTime != DateTime.MinValue) {
                fileSystem.SetLastWriteTimeUtc(fileLocation.LocalPath, fileTime);
            }
            if (!(checkedOut.HasValue && checkedOut.Value)) {
                fileSystem.SetAttributes(fileLocation.LocalPath, FileAttributes.ReadOnly);
            }
        }
        public class FileDataLocation : FileLocation {
            public byte[] Data;
            public string FileName {
                get { return HelperPaths.GetFile(this.Path); }
            }
            public FileDataLocation(string path, string localPath, byte[] data)
                : base(path, localPath) {
                this.Data = data;
            }
            public FileDataLocation(string path, string localPath, FileStateInfo info, byte[] data)
                : base(path, localPath, info) {
                this.Data = data;
            }
        }
        DateTime SelectFileTime(FileStateInfo info, FileTime fileTimeState) {
            switch (fileTimeState) {
                case FileTime.CheckIn:
                    return info.VersionDate;
                case FileTime.Modification:
                    return info.ModifiedOn;
            }
            return DateTime.UtcNow;
        }
        public class VcsClientBatchState {
            public ActionToWritableCopy ActionToExistFile = new ActionToWritableCopy(ActionWithCopy.Replace, false);
            public ActionToWritableCopy ActionToCheckedOutFile = new ActionToWritableCopy(ActionWithCopy.Leave, false);
        }
        public enum ActionWithCopy {
            Leave, Replace, CheckOut, CheckOutMerge, Merge
        };
        public class ActionToWritableCopy {
            bool applyToAll;
            ActionWithCopy action;

            public ActionWithCopy Action {
                get { return action; }
            }
            public bool ApplyToAll {
                get { return applyToAll; }
            }
            public ActionToWritableCopy(ActionWithCopy action, bool applyToAll) {
                this.action = action;
                this.applyToAll = applyToAll;
            }
        }
        public class FileLocation {
            public string Path;
            public string LocalPath;
            public FileStateInfo? Info;
            public FileLocation(string path, string localPath) {
                this.Path = path;
                this.LocalPath = localPath;
            }
            public FileLocation(string path, string localPath, FileStateInfo info) : this(path, localPath) {
                this.Info = info;
            }
        }
        bool EqualsCheckOutParams(FileStateInfo fsi, FileLocation fl, out bool differentHost) {
            return EqualsCheckOutParams(fsi, fl.LocalPath, out differentHost);
        }
        bool EqualsCheckOutParams(FileStateInfo fsi, string localPath, out bool differentHost) {
            differentHost = !string.Equals(fsi.CheckOutHost, Environment.MachineName, StringComparison.CurrentCultureIgnoreCase);
            return string.Equals(fsi.CheckOutFolder, HelperPaths.GetDirectory(localPath), StringComparison.CurrentCultureIgnoreCase)
                && !differentHost;
        }
        public bool GetLocalFileModified(FileStateInfo info, string localPath, string vcsPath, DateTime projectDate, out FileBaseInfoState fileState, DateTime lastWrite) {
            try {
                using (Stream fileStream = fileSystem.OpenRead(localPath)) {
                    byte[] localHash = DXVCSHelpers.GetHashMD5(fileStream);
                    bool hashEquals = DXVCSHelpers.BytesAreEquals(localHash, info.Hash);
                    fileState = FileBaseInfoState.Modified;
                    return !hashEquals;
                }
            }
            catch (IOException) {
                fileState = FileBaseInfoState.Locked;
                return false;
            }
        }
        void CreateDirectory(string curLocalFolder) {
            fileSystem.CreateDirectory(curLocalFolder);
        }
        static void ProcessException(Queue<Exception> exceptionQueue) {
            lock (exceptionQueue) {
                if (exceptionQueue.Count > 0) {
                    Exception ex = exceptionQueue.Dequeue();
                    if (ex != null)
                        throw new DXVCSException("In process thread error.", ex);
                }
            }
        }
        ManualResetEvent StartTouchThread(string id) {
            ManualResetEvent waitEvent = new ManualResetEvent(false);
            ThreadPool.QueueUserWorkItem(new WaitCallback(delegate (object o) {
                try {
                    while (!waitEvent.WaitOne(30000, false)) {
                        Service.TouchSession(id);
                    }
                }
                catch (Exception) { }
            }));
            return waitEvent;
        }
        void QueueBlock(string id, AutoResetEvent getBlockEvent, BlockInfo info, Queue<Exception> exceptionQueue) {
            try {
                info.Last = Service.GetNextBlockInfo2(id, out info.BlockIndex, out info.Paths, out info.Info, out info.MagicPos);
            }
            catch (DXVCSGetNextTimeoutException) { }
            catch (Exception ex) {
                lock (exceptionQueue) {
                    exceptionQueue.Enqueue(ex);
                }
            }
            getBlockEvent.Set();
        }

        public void CheckOutFile(string vcsFile, string localFile, string comment, bool dontGetLocalCopy = false) {
            if (string.IsNullOrEmpty(vcsFile))
                throw new ArgumentException("vcsFile");

            if (string.IsNullOrEmpty(localFile))
                throw new ArgumentException("localFile");

            bool getLocalCopy = !dontGetLocalCopy && (!File.Exists(localFile) || !Service.GetFile(vcsFile).CheckedOutMe);
            Service.CheckOut(Environment.MachineName, new[] { vcsFile }, new[] { Path.GetDirectoryName(localFile) }, new[] { comment }, null);
            if (File.Exists(localFile))
                File.SetAttributes(localFile, FileAttributes.Normal);

            if (getLocalCopy) {
                GetLatestFileVersion(vcsFile, localFile);
            }
        }

        public void CheckInFile(string vcsFile, string localFile, string comment) {
            if (string.IsNullOrEmpty(vcsFile))
                throw new ArgumentException("vcsFile");

            if (string.IsNullOrEmpty(localFile))
                throw new ArgumentException("localFile");

            if (!Service.GetFile(vcsFile).CheckedOutMe)
                throw new InvalidOperationException("Can't check-in: the file is not checked out: " + vcsFile);

            var data = new byte[1][] { File.ReadAllBytes(localFile) };
            string result = Service.CheckIn(new[] { vcsFile }, data, new[] { File.GetLastWriteTimeUtc(localFile) }, new[] { comment }, false);
            File.SetAttributes(localFile, File.GetAttributes(localFile) | FileAttributes.ReadOnly);
        }

        public string GetFileWorkingPath(string vcsFile) {
            string workingFolder = GetWorkingFolder(Path.GetDirectoryName(vcsFile).Replace("\\", "/"));
            if (string.IsNullOrEmpty(workingFolder))
                return null;

            return Path.Combine(workingFolder, Path.GetFileName(vcsFile));
        }
        string GetWorkingFolder(string vcsProject) {
            if (string.IsNullOrEmpty(vcsProject))
                throw new ArgumentException("vcsProject");

            return Service.GetWorkingFolder(Environment.MachineName, vcsProject);
        }
        public void UndoCheckout(string vcsFile, string localFile) {
            if (string.IsNullOrEmpty(vcsFile))
                throw new ArgumentException("vcsFile");
            if (string.IsNullOrEmpty(localFile))
                throw new ArgumentException("localFile");

            if (!Service.GetFile(vcsFile).CheckedOutMe)
                throw new InvalidOperationException("Can't undo check out: the file is not checked out: " + vcsFile);
            Service.UndoCheckOut(new[] { vcsFile }, new[] { false });
        }
        public void AddFile(string vcsFile, byte[] fileBytes, string comment) {
            if (string.IsNullOrEmpty(vcsFile))
                throw new ArgumentException("vcsFile");
            var folders = vcsFile.Split(@"/".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
            var queue = new Queue<string>(folders);
            string temp = queue.Dequeue();
            while (queue.Count > 0) {
                string folder = queue.Dequeue();
                if (queue.Count == 0)
                    CreateFile(temp, folder, fileBytes, comment);
                else
                    CreateProject(temp, folder, comment);
                temp += @"/" + folder;
            }
        }
        public void AddProject(string vcsPath, string comment) {
            if (string.IsNullOrEmpty(vcsPath))
                throw new ArgumentException("vcsFile");
            var folders = vcsPath.Split(@"/".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
            var queue = new Queue<string>(folders);
            string temp = queue.Dequeue();
            while (queue.Count > 0) {
                string folder = queue.Dequeue();
                CreateProject(temp, folder, comment);
                temp += @"/" + folder;
            }

        }
        public void DeleteFile(string vcsPath) {
            if (string.IsNullOrEmpty(vcsPath))
                throw new ArgumentException("vcsFile");
            Service.SetDeletedFile(vcsPath, false);
        }
        public void DeleteProject(string vcsPath) {
            if (string.IsNullOrEmpty(vcsPath))
                throw new ArgumentException("vcsPath");
            Service.SetDeletedProject(vcsPath);
        }
        public void MoveFile(string vcsPath, string newVcsPath, string comment) {
            if (string.IsNullOrEmpty(vcsPath))
                throw new ArgumentException("vcsPath");
            if (string.IsNullOrEmpty(newVcsPath))
                throw new ArgumentException("newVcsPath");
            string oldProjectPath = GetProjectPath(vcsPath);
            string newProjectPath = GetProjectPath(newVcsPath);
            if (oldProjectPath != newProjectPath) {
                AddProject(newProjectPath, comment);
                string[] exist;
                Service.MoveFiles(new[] { vcsPath }, newProjectPath, out exist);
                if (exist.Length > 0)
                    if (!exist.All(x => SafeDeleteFile(GetProjectPath(x), GetFileName(x))))
                        throw new ArgumentException("move file failed");
                    else
                        Service.MoveFiles(new[] { vcsPath }, newProjectPath, out exist);
                var projectFiles = Service.GetFiles(oldProjectPath);
                if (projectFiles.Length == 0)
                    DeleteProject(oldProjectPath);
                return;
            }
            string oldFileName = GetFileName(vcsPath);
            string newFileName = GetFileName(newVcsPath);
            if (oldFileName != newFileName) {
                try {
                    Service.RenameFile(vcsPath, newFileName);
                }
                catch (DXVCSFileAlreadyExistsException) {
                    if (!SafeDeleteFile(newProjectPath, newFileName))
                        throw;
                    Service.RenameFile(vcsPath, newFileName);
                }
            }
        }
        string GetProjectPath(string vcsPath) {
            return Path.GetDirectoryName(vcsPath).Replace("\\", "/");
        }
        string GetFileName(string vcsPath) {
            return Path.GetFileName(vcsPath);
        }
        bool IsProject(string vcsPath) {
            var project = Service.FindProject(vcsPath);
            if (!project.IsNull)
                return true;
            return false;
        }
        void CreateFile(string vcsFile, string fileName, byte[] fileBytes, string comment) {
            if (string.IsNullOrEmpty(vcsFile))
                throw new ArgumentException("vcsFile");
            if (string.IsNullOrEmpty(fileName))
                throw new ArgumentException("fileName");
            try {
                if (!IsUnderVss(vcsFile))
                    Service.CreateFile(vcsFile, fileName, fileBytes, DateTime.Now, comment);
            }
            catch (DXVCSFileAlreadyExistsException) {
                if (SafeDeleteFile(vcsFile, fileName))
                    Service.CreateFile(vcsFile, fileName, fileBytes, DateTime.Now, comment);
                else
                    throw;
            }
        }
        bool SafeDeleteFile(string vcsPath, string fileName) {
            var fileStateInfo = Service.GetDeletedFiles(vcsPath);
            var fileInfo = fileStateInfo.Where(x => x.Name == fileName).FirstOrDefault();
            if (fileInfo.IsNull)
                return false;
            string path = $@"{vcsPath}/{fileName}";
            Service.RecoverDeletedFile(path);
            string newFileName = fileName + "_deleted_" + Guid.NewGuid();
            Service.RenameFile(path, newFileName);
            Service.SetDeletedFile($@"{vcsPath}/{newFileName}", false);
            return true;
        }
        bool SafeDeleteProject(string vcsPath, string name) {
            var projectStateInfo = Service.GetDeletedProjects(vcsPath);
            var fileInfo = projectStateInfo.Where(x => x.Name == name).FirstOrDefault();
            if (fileInfo.IsNull || string.IsNullOrEmpty(fileInfo.Name))
                return false;
            string path = $@"{vcsPath}/{name}";
            Service.RecoverDeletedProject(path);
            string newProjectName = name + "_deleted_" + Guid.NewGuid();
            Service.RenameProject(path, newProjectName);
            Service.SetDeletedProject($@"{vcsPath}/{newProjectName}");
            return true;
        }
        void CreateProject(string vcsPath, string name, string comment) {
            if (string.IsNullOrEmpty(vcsPath))
                throw new ArgumentException("vcsFile");
            try {
                if (!IsUnderVss($@"{vcsPath}/{name}"))
                    Service.CreateProject(vcsPath, name, comment, true);
            }
            catch (DXVCSProjectAlreadyExistsException) {
                if (!SafeDeleteProject(vcsPath, name))
                    throw;
                Service.CreateProject(vcsPath, name, comment, true);
            }
        }
        public bool IsUnderVss(string vcsFile) {
            if (string.IsNullOrEmpty(vcsFile))
                throw new ArgumentException("vcsFile");

            var project = Service.FindProject(vcsFile);
            if (!project.IsNull)
                return true;
            var file = Service.FindFile(vcsFile);
            return !file.IsNull;
        }
        public FileStateInfo GetFile(string vcsFile) {
            if (string.IsNullOrEmpty(vcsFile))
                throw new ArgumentException("vcsFile");
            return Service.GetFile(vcsFile);
        }
        public bool IsCheckedOut(string vcsFile) {
            return GetFile(vcsFile).CheckedOut;
        }
        public bool IsCheckedOutByMe(string vcsFile) {
            return GetFile(vcsFile).CheckedOutMe;
        }
        public void CreateLabel(string vcsPath, string labelName, string comment) {
            if (string.IsNullOrEmpty(vcsPath))
                throw new ArgumentException("vcsPath");
            var labels = Service.GetLabels(vcsPath);
            if (labels.Any(x => x.Name == labelName))
                Service.DeleteLabel(vcsPath, labelName);
            Service.CreateLabel(vcsPath, labelName, comment);
        }
    }
}