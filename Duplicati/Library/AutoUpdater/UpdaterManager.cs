// Copyright (C) 2024, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
// 
// Permission is hereby granted, free of charge, to any person obtaining a 
// copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.

using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Duplicati.Library.Interface;
using Duplicati.Library.Common.IO;
using Duplicati.Library.Common;
using System.Diagnostics;

namespace Duplicati.Library.AutoUpdater
{
    public enum AutoUpdateStrategy
    {
        CheckBefore,
        CheckDuring,
        CheckAfter,
        InstallBefore,
        InstallDuring,
        InstallAfter,
        Never
    }

    public static class UpdaterManager
    {
        /// <summary>
        /// The magic exit code that signals an update has been installed and that the app should restart
        /// </summary>
        public const int MAGIC_EXIT_CODE = 126;

        private static readonly System.Security.Cryptography.RSACryptoServiceProvider SIGN_KEY = AutoUpdateSettings.SignKey;
        private static readonly string[] MANIFEST_URLS = AutoUpdateSettings.URLs;
        private static readonly string APPNAME = AutoUpdateSettings.AppName;

        public static readonly string INSTALLDIR;

        private static readonly string INSTALLED_BASE_DIR =
            string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(string.Format(BASEINSTALLDIR_ENVNAME_TEMPLATE, APPNAME)))
            ? System.IO.Path.GetDirectoryName(Duplicati.Library.Utility.Utility.getEntryAssembly().Location)
            : Environment.ExpandEnvironmentVariables(System.Environment.GetEnvironmentVariable(string.Format(BASEINSTALLDIR_ENVNAME_TEMPLATE, APPNAME)));

        private static readonly bool DISABLE_UPDATE_DOMAIN = Debugger.IsAttached || Utility.Utility.ParseBool(Environment.GetEnvironmentVariable(string.Format(SKIPUPDATE_ENVNAME_TEMPLATE, APPNAME)), false);

        public static bool RequiresRespawn { get; set; }

        public static bool IgnoreWebrootFolder { get; set; }

        private static KeyValuePair<string, UpdateInfo>? m_hasUpdateInstalled;

        public static readonly UpdateInfo SelfVersion;

        public static readonly UpdateInfo BaseVersion;

        public static event Action<Exception> OnError;

        private const string DATETIME_FORMAT = "yyyymmddhhMMss";
        private const string BASEINSTALLDIR_ENVNAME_TEMPLATE = "AUTOUPDATER_{0}_INSTALL_ROOT";
        private const string UPDATEINSTALLDIR_ENVNAME_TEMPLATE = "AUTOUPDATER_{0}_UPDATE_ROOT";
        internal const string SKIPUPDATE_ENVNAME_TEMPLATE = "AUTOUPDATER_{0}_SKIP_UPDATE";
        private const string RUN_UPDATED_FOLDER_PATH = "AUTOUPDATER_LOAD_UPDATE";
        private const string SLEEP_ENVNAME_TEMPLATE = "AUTOUPDATER_{0}_SLEEP";
        internal const string UPDATE_STRATEGY_ENVNAME_TEMPLATE = "AUTOUPDATER_{0}_POLICY";
        private const string UPDATE_MANIFEST_FILENAME = "autoupdate.manifest";
        private const string README_FILE = "README.txt";
        private const string INSTALL_FILE = "installation.txt";
        private const string CURRENT_FILE = "current";

        /// <summary>
        /// Gets the original directory that this application was installed into
        /// </summary>
        /// <value>The original directory that this application was installed into</value>
        public static string InstalledBaseDir { get { return INSTALLED_BASE_DIR; } }

        /// <summary>
        /// Gets the last version found from an update
        /// </summary>
        public static UpdateInfo LastUpdateCheckVersion { get; private set; }

        static UpdaterManager()
        {
            // Update folder strategy is a bit complicated,
            // because it depends on the actual system,
            // and because it tries to find a good spot
            // by probing for locations

            // The "overrides" paths are checked,
            // to see if they exist and are writeable.
            // The first existing and writeable path
            // for "overrides" is chosen

            // If override was not found, the "legacypaths"
            // are checked in the same way to see if
            // we have previously used such a folder
            // and if that folder has contents,
            // which indicates that it has been used.

            // Finally we check the "attempts",
            // which are suitable candidates
            // for storing the updates on each
            // operating system

            if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(string.Format(UPDATEINSTALLDIR_ENVNAME_TEMPLATE, APPNAME))))
            {
                string installdir = null;
                var programfiles = System.Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

                // The user can override updates by having a local updates folder
                var overrides = new List<string>(new string[] {
                        System.IO.Path.Combine(InstalledBaseDir, "updates"),
                    });

                if (Platform.IsClientWindows)
                {
                    overrides.Add(System.IO.Path.Combine(System.Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), APPNAME, "updates"));
                    overrides.Add(System.IO.Path.Combine(System.Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), APPNAME, "updates"));
                }
                else
                {
                    if (Platform.IsClientOSX)
                        overrides.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Application Support", APPNAME, "updates"));

                    overrides.Add(System.IO.Path.Combine(System.Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), APPNAME, "updates"));
                }

                // Previous locations that we don't want to use,
                // but we keep them active to avoid breaking the update system
                var legacypaths = new List<string>();

                if (!string.IsNullOrWhiteSpace(programfiles))
                    legacypaths.Add(System.IO.Path.Combine(programfiles, APPNAME, "updates"));
                if (Platform.IsClientPosix)
                    legacypaths.Add(System.IO.Path.Combine(System.Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), APPNAME, "updates"));

                // The real attempts that we probe for
                var attempts = new List<string>();

                // We do not want to install anything in the basedir, if the application is installed in "ProgramFiles"
                if (!string.IsNullOrWhiteSpace(programfiles) && !InstalledBaseDir.StartsWith(Util.AppendDirSeparator(programfiles), StringComparison.Ordinal))
                    attempts.Add(System.IO.Path.Combine(InstalledBaseDir, "updates"));

                if (Platform.IsClientOSX)
                    attempts.Add(System.IO.Path.Combine("/", "Library", "Application Support", APPNAME, "updates"));
                else
                    attempts.Add(System.IO.Path.Combine(System.Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), APPNAME, "updates"));

                attempts.AddRange(overrides.Skip(1));

                // Check if the override folder exists, and choose that
                foreach (var p in overrides)
                    if (!string.IsNullOrWhiteSpace(p) && System.IO.Directory.Exists(p) && TestDirectoryIsWriteable(p))
                    {
                        installdir = p;
                        break;
                    }

                if (string.IsNullOrWhiteSpace(installdir))
                    foreach (var p in legacypaths)
                        if (!string.IsNullOrWhiteSpace(p) && System.IO.Directory.Exists(p) && System.IO.Directory.EnumerateFiles(p, "*", System.IO.SearchOption.TopDirectoryOnly).Any() && TestDirectoryIsWriteable(p))
                        {
                            installdir = p;
                            break;
                        }

                if (string.IsNullOrWhiteSpace(installdir))
                    foreach (var p in attempts)
                        if (!string.IsNullOrWhiteSpace(p) && TestDirectoryIsWriteable(p))
                        {
                            installdir = p;
                            break;
                        }

                INSTALLDIR = installdir;
            }
            else
            {
                INSTALLDIR = Environment.ExpandEnvironmentVariables(System.Environment.GetEnvironmentVariable(string.Format(UPDATEINSTALLDIR_ENVNAME_TEMPLATE, APPNAME)));
            }


            if (INSTALLDIR != null)
            {
                if (!System.IO.File.Exists(System.IO.Path.Combine(INSTALLDIR, README_FILE)))
                    System.IO.File.WriteAllText(System.IO.Path.Combine(INSTALLDIR, README_FILE), AutoUpdateSettings.UpdateFolderReadme);
                if (!System.IO.File.Exists(System.IO.Path.Combine(INSTALLDIR, INSTALL_FILE)))
                    System.IO.File.WriteAllText(System.IO.Path.Combine(INSTALLDIR, INSTALL_FILE), AutoUpdateSettings.UpdateInstallFileText);
            }

            UpdateInfo selfVersion = null;
            UpdateInfo baseVersion = null;
            try
            {
                selfVersion = ReadInstalledManifest(System.IO.Path.GetDirectoryName(Duplicati.Library.Utility.Utility.getEntryAssembly().Location));
            }
            catch
            {
            }

            try
            {
                baseVersion = ReadInstalledManifest(InstalledBaseDir);
            }
            catch
            {
            }

            if (selfVersion == null)
            {
                selfVersion = new UpdateInfo() {
                    Displayname = string.IsNullOrWhiteSpace(Duplicati.License.VersionNumbers.TAG) ? "Current" : Duplicati.License.VersionNumbers.TAG,
                    Version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(),
                    ReleaseTime = new DateTime(0),
                    ReleaseType =
#if DEBUG
                        "Debug"
#else
                        string.IsNullOrWhiteSpace(AutoUpdateSettings.BuildUpdateChannel) ? "Nightly" : AutoUpdateSettings.BuildUpdateChannel
#endif
                };


            }

            if (baseVersion == null)
                baseVersion = selfVersion;

            SelfVersion = selfVersion;
            BaseVersion = baseVersion;
        }

        public static Version TryParseVersion(string str)
        {
            Version v;
            if (Version.TryParse(str, out v))
                return v;
            else
                return new Version(0, 0);
        }

        public static bool HasUpdateInstalled
        {
            get
            {
                if (!m_hasUpdateInstalled.HasValue)
                {
                    var selfversion = TryParseVersion(SelfVersion.Version);

                    m_hasUpdateInstalled =
                        (from n in FindInstalledVersions()
                         let nversion = TryParseVersion(n.Value.Version)
                         let newerVersion = selfversion < nversion
                         where newerVersion && VerifyUnpackedFolder(n.Key, n.Value)
                         orderby nversion descending
                         select n)
                            .FirstOrDefault();
                }

                return m_hasUpdateInstalled.Value.Value != null;
            }
        }

        private static bool TestDirectoryIsWriteable(string path)
        {
            var p2 = System.IO.Path.Combine(path, "test-" + DateTime.UtcNow.ToString(DATETIME_FORMAT, System.Globalization.CultureInfo.InvariantCulture));
            var probe = System.IO.Directory.Exists(path) ? p2 : path;

            if (!System.IO.Directory.Exists(probe))
            {
                try
                {
                    System.IO.Directory.CreateDirectory(probe);
                    if (probe != path)
                        System.IO.Directory.Delete(probe);
                    return true;
                }
                catch
                {
                }
            }

            return false;
        }

        public static string InstallID
        {
            get
            {
                try { return System.IO.File.ReadAllText(System.IO.Path.Combine(INSTALLDIR, INSTALL_FILE)).Replace('\r', '\n').Split(new char[] { '\n' }).FirstOrDefault().Trim() ?? ""; }
                catch { }

                return "";
            }
        }

        public static UpdateInfo CheckForUpdate(ReleaseType channel = ReleaseType.Unknown)
        {
            if (channel == ReleaseType.Unknown)
                channel = AutoUpdateSettings.DefaultUpdateChannel;

            foreach (var rawurl in MANIFEST_URLS)
            {
                var url = rawurl;

                // Attempt to match the url to change the channel if possible
                // This allows overrides to the URLs for deployment of custom builds,
                // but does not require that they adopt the channel system
                var match = AutoUpdateSettings.MATCH_AUTOUPDATE_URL.Match(url);
                if (match.Success)
                {
                    var mg = match.Groups[AutoUpdateSettings.MATCH_UPDATE_URL_CHANNEL_GROUP];

                    // Replace the channel name with the chosen channel
                    url =
                        url.Substring(0, mg.Index)
                        +
                        channel.ToString().ToLowerInvariant()
                        +
                        url.Substring(mg.Index + mg.Length);
                }

                try
                {
                    using (var tmpfile = new Library.Utility.TempFile())
                    {
                        System.Net.WebClient wc = new System.Net.WebClient();
                        wc.Headers.Add(System.Net.HttpRequestHeader.UserAgent, string.Format("{0} v{1}{2}", APPNAME, SelfVersion.Version, string.IsNullOrWhiteSpace(InstallID) ? "" : " -" + InstallID));
                        wc.Headers.Add("X-Install-ID", InstallID);
                        wc.DownloadFile(url, tmpfile);

                        using (var fs = System.IO.File.OpenRead(tmpfile))
                        using (var ss = new SignatureReadingStream(fs, SIGN_KEY))
                        using (var tr = new System.IO.StreamReader(ss))
                        using (var jr = new Newtonsoft.Json.JsonTextReader(tr))
                        {
                            var update = new Newtonsoft.Json.JsonSerializer().Deserialize<UpdateInfo>(jr);

                            if (TryParseVersion(update.Version) <= TryParseVersion(SelfVersion.Version))
                                return null;

                            // Don't install a debug update on a release build and vice versa
                            if (string.Equals(SelfVersion.ReleaseType, "Debug", StringComparison.OrdinalIgnoreCase) && !string.Equals(update.ReleaseType, SelfVersion.ReleaseType, StringComparison.CurrentCultureIgnoreCase))
                                return null;

                            ReleaseType rt;
                            if (!Enum.TryParse<ReleaseType>(update.ReleaseType, true, out rt))
                                rt = ReleaseType.Unknown;

                            // If the update is too low to be considered, skip it
                            // Should never happen, but protects against mistakes in deployment
                            if (rt > channel)
                                return null;

                            LastUpdateCheckVersion = update;
                            return update;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (OnError != null)
                        OnError(ex);
                }
            }

            return null;
        }

        private static UpdateInfo ReadInstalledManifest(string folder)
        {
            var manifest = System.IO.Path.Combine(folder, UPDATE_MANIFEST_FILENAME);
            if (System.IO.File.Exists(manifest))
            {
                try
                {
                    using (var fs = System.IO.File.OpenRead(manifest))
                    using (var ss = new SignatureReadingStream(fs, SIGN_KEY))
                    using (var tr = new System.IO.StreamReader(ss))
                    using (var jr = new Newtonsoft.Json.JsonTextReader(tr))
                        return new Newtonsoft.Json.JsonSerializer().Deserialize<UpdateInfo>(jr);
                }
                catch (Exception ex)
                {
                    if (OnError != null)
                        OnError(ex);
                }
            }

            return null;
        }

        public static IEnumerable<KeyValuePair<string, UpdateInfo>> FindInstalledVersions()
        {
            var res = new List<KeyValuePair<string, UpdateInfo>>();
            if (INSTALLDIR != null)
                foreach (var folder in SystemIO.IO_OS.GetDirectories(INSTALLDIR))
                {
                    var r = ReadInstalledManifest(folder);
                    if (r != null)
                        res.Add(new KeyValuePair<string, UpdateInfo>(folder, r));
                }

            return res;
        }

        public static bool DownloadAndUnpackUpdate(UpdateInfo version, Action<double> progress = null)
        {
            if (INSTALLDIR == null)
                return false;


            var updates = version.RemoteURLS.ToList();

            // If alternate update URLs are specified,
            // we look for packages there as well
            if (AutoUpdateSettings.UsesAlternateURLs)
            {
                var packagepath = new Library.Utility.Uri(updates[0]).Path;
                var packagename = packagepath.Split('/').Last();

                foreach (var alt_url in AutoUpdateSettings.URLs.Reverse())
                {
                    var alt_uri = new Library.Utility.Uri(alt_url);
                    var path_components = alt_uri.Path.Split('/');
                    var path = string.Join("/", path_components.Take(path_components.Count() - 1).Union(new string[] { packagename }));

                    var new_path = alt_uri.SetPath(path);
                    updates.Insert(0, new_path.ToString());
                }
            }

            using (var tempfilename = new Library.Utility.TempFile())
            {
                foreach (var url in updates)
                {
                    try
                    {
                        using (var tempfile = System.IO.File.Open(tempfilename, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                        {
                            Action<long> cb = null;
                            if (progress != null)
                                cb = (s) => { progress(Math.Min(1.0, Math.Max(0.0, (double)s / version.CompressedSize))); };

                            var wreq = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                            wreq.UserAgent = string.Format("{0} v{1}", APPNAME, SelfVersion.Version);
                            wreq.Headers.Add("X-Install-ID", InstallID);

                            var areq = new Duplicati.Library.Utility.AsyncHttpRequest(wreq);
                            using (var resp = areq.GetResponse())
                            using (var rss = areq.GetResponseStream())
                            using (var pgs = new Duplicati.Library.Utility.ProgressReportingStream(rss, cb))
                            {
                                Duplicati.Library.Utility.Utility.CopyStream(pgs, tempfile);
                            }

                            var sha256 = System.Security.Cryptography.SHA256.Create();
                            var md5 = System.Security.Cryptography.MD5.Create();

                            if (tempfile.Length != version.CompressedSize)
                                throw new Exception(string.Format("Invalid file size {0}, expected {1} for {2}", tempfile.Length, version.CompressedSize, url));

                            tempfile.Position = 0;
                            var sha256hash = Convert.ToBase64String(sha256.ComputeHash(tempfile));
                            if (sha256hash != version.SHA256)
                                throw new Exception(string.Format("Damaged or corrupted file, sha256 mismatch for {0}", url));


                            tempfile.Position = 0;
                            var md5hash = Convert.ToBase64String(md5.ComputeHash(tempfile));
                            if (md5hash != version.MD5)
                                throw new Exception(string.Format("Damaged or corrupted file, md5 mismatch for {0}", url));

                            tempfile.Position = 0;
                            using (var tempfolder = new Duplicati.Library.Utility.TempFolder())
                            using (ICompression zip = new Duplicati.Library.Compression.FileArchiveZip(tempfile, ArchiveMode.Read, new Dictionary<string, string>()))
                            {
                                foreach (var file in zip.ListFilesWithSize(""))
                                {
                                    if (System.IO.Path.IsPathRooted(file.Key) || file.Key.Trim().StartsWith("..", StringComparison.OrdinalIgnoreCase))
                                        throw new Exception(string.Format("Out-of-place file path detected: {0}", file.Key));

                                    var targetpath = System.IO.Path.Combine(tempfolder, file.Key);
                                    var targetfolder = System.IO.Path.GetDirectoryName(targetpath);
                                    if (!System.IO.Directory.Exists(targetfolder))
                                        System.IO.Directory.CreateDirectory(targetfolder);

                                    using (var zs = zip.OpenRead(file.Key))
                                    using (var fs = System.IO.File.Create(targetpath))
                                        zs.CopyTo(fs);
                                }

                                if (VerifyUnpackedFolder(tempfolder, version))
                                {
                                    var versionstring = TryParseVersion(version.Version).ToString();
                                    var targetfolder = System.IO.Path.Combine(INSTALLDIR, versionstring);
                                    if (System.IO.Directory.Exists(targetfolder))
                                        System.IO.Directory.Delete(targetfolder, true);

                                    System.IO.Directory.CreateDirectory(targetfolder);

                                    var tempfolderpath = Util.AppendDirSeparator(tempfolder);
                                    var tempfolderlength = tempfolderpath.Length;

                                    // Would be nice, but does not work :(
                                    //System.IO.Directory.Move(tempfolder, targetfolder);

                                    foreach (var e in Duplicati.Library.Utility.Utility.EnumerateFileSystemEntries(tempfolder))
                                    {
                                        var relpath = e.Substring(tempfolderlength);
                                        if (string.IsNullOrWhiteSpace(relpath))
                                            continue;

                                        var fullpath = System.IO.Path.Combine(targetfolder, relpath);
                                        if (relpath.EndsWith(Util.DirectorySeparatorString, StringComparison.Ordinal))
                                            System.IO.Directory.CreateDirectory(fullpath);
                                        else
                                            System.IO.File.Copy(e, fullpath);
                                    }

                                    // Verification will kick in when we list the installed updates
                                    //VerifyUnpackedFolder(targetfolder, version);
                                    System.IO.File.WriteAllText(System.IO.Path.Combine(INSTALLDIR, CURRENT_FILE), versionstring);

                                    m_hasUpdateInstalled = null;

                                    var obsolete = (from n in FindInstalledVersions()
                                                    where n.Value.Version != version.Version && n.Value.Version != SelfVersion.Version
                                                    let x = TryParseVersion(n.Value.Version)
                                                    orderby x descending
                                                    select n).Skip(1).ToArray();

                                    foreach (var f in obsolete)
                                        try { System.IO.Directory.Delete(f.Key, true); }
                                        catch { }

                                    return true;
                                }
                                else
                                {
                                    throw new Exception(string.Format("Unable to verify unpacked folder for url: {0}", url));
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (OnError != null)
                            OnError(ex);
                    }
                }
            }

            return false;
        }

        public static bool VerifyUnpackedFolder(string folder, UpdateInfo version = null)
        {
            try
            {
                UpdateInfo update;
                FileEntry manifest;

                var sha256 = System.Security.Cryptography.SHA256.Create();
                var md5 = System.Security.Cryptography.MD5.Create();

                using (var fs = System.IO.File.OpenRead(System.IO.Path.Combine(folder, UPDATE_MANIFEST_FILENAME)))
                {
                    using (var ss = new SignatureReadingStream(fs, SIGN_KEY))
                    using (var tr = new System.IO.StreamReader(ss))
                    using (var jr = new Newtonsoft.Json.JsonTextReader(tr))
                        update = new Newtonsoft.Json.JsonSerializer().Deserialize<UpdateInfo>(jr);

                    sha256.Initialize();
                    md5.Initialize();

                    fs.Position = 0;
                    var h1 = Convert.ToBase64String(sha256.ComputeHash(fs));
                    fs.Position = 0;
                    var h2 = Convert.ToBase64String(md5.ComputeHash(fs));

                    manifest = new FileEntry() {
                        Path = UPDATE_MANIFEST_FILENAME,
                        Ignore = false,
                        LastWriteTime = update.ReleaseTime,
                        SHA256 = h1,
                        MD5 = h2
                    };
                }

                if (version != null && (update.Displayname != version.Displayname || update.ReleaseTime != version.ReleaseTime))
                    throw new Exception("The found version was not the expected version");

                var paths = update.Files.Where(x => !x.Ignore).ToDictionary(x => x.Path.Replace('/', System.IO.Path.DirectorySeparatorChar), Library.Utility.Utility.ClientFilenameStringComparer);
                paths.Add(manifest.Path, manifest);

                var ignores = (from x in update.Files where x.Ignore select Util.AppendDirSeparator(x.Path.Replace('/', System.IO.Path.DirectorySeparatorChar))).ToList();

                folder = Util.AppendDirSeparator(folder);
                var baselen = folder.Length;

                foreach (var file in Library.Utility.Utility.EnumerateFileSystemEntries(folder))
                {
                    var relpath = file.Substring(baselen);
                    if (string.IsNullOrWhiteSpace(relpath))
                        continue;

                    if (IgnoreWebrootFolder && relpath.StartsWith("webroot", Library.Utility.Utility.ClientFilenameStringComparison))
                        continue;

                    FileEntry fe;
                    if (!paths.TryGetValue(relpath, out fe))
                    {
                        var ignore = false;
                        foreach (var c in ignores)
                            if (ignore = relpath.StartsWith(c, Library.Utility.Utility.ClientFilenameStringComparison))
                                break;

                        if (ignore)
                            continue;

                        throw new Exception(string.Format("Found unexpected file: {0}", file));
                    }

                    paths.Remove(relpath);

                    if (fe.Path.EndsWith("/", StringComparison.Ordinal))
                        continue;

                    sha256.Initialize();
                    md5.Initialize();

                    using (var fs = System.IO.File.OpenRead(file))
                    {
                        if (Convert.ToBase64String(sha256.ComputeHash(fs)) != fe.SHA256)
                            throw new Exception(string.Format("Invalid sha256 hash for file: {0}", file));

                        fs.Position = 0;
                        if (Convert.ToBase64String(md5.ComputeHash(fs)) != fe.MD5)
                            throw new Exception(string.Format("Invalid md5 hash for file: {0}", file));
                    }
                }

                var filteredpaths = paths
                    .Where(p => !string.IsNullOrWhiteSpace(p.Key) && !p.Key.EndsWith("/", StringComparison.Ordinal))
                    .Where(p => !IgnoreWebrootFolder || !p.Key.StartsWith("webroot", Library.Utility.Utility.ClientFilenameStringComparison))
                    .Select(p => p.Key)
                    .ToList();

                if (filteredpaths.Count == 1)
                    throw new Exception(string.Format("Folder {0} is missing: {1}", folder, filteredpaths.First()));
                else if (filteredpaths.Count > 0)
                    throw new Exception(string.Format("Folder {0} is missing {1} and {2} other file(s)", folder, filteredpaths.First(), filteredpaths.Count - 1));

                return true;
            }
            catch (Exception ex)
            {
                if (OnError != null)
                    OnError(ex);
            }

            return false;
        }

        public static bool SetRunUpdate()
        {
            if (HasUpdateInstalled)
            {
                AppDomain.CurrentDomain.SetData(RUN_UPDATED_FOLDER_PATH, m_hasUpdateInstalled.Value.Key);
                return true;
            }

            return false;
        }

        public static void CreateUpdatePackage(System.Security.Cryptography.RSACryptoServiceProvider key, string inputfolder, string outputfolder, string manifest = null)
        {
            // Read the existing manifest

            UpdateInfo remoteManifest;

            var manifestpath = manifest ?? System.IO.Path.Combine(inputfolder, UPDATE_MANIFEST_FILENAME);

            using (var s = System.IO.File.OpenRead(manifestpath))
            using (var sr = new System.IO.StreamReader(s))
            using (var jr = new Newtonsoft.Json.JsonTextReader(sr))
                remoteManifest = new Newtonsoft.Json.JsonSerializer().Deserialize<UpdateInfo>(jr);

            if (remoteManifest.Files == null)
                remoteManifest.Files = new FileEntry[0];

            if (remoteManifest.ReleaseTime.Ticks == 0)
                remoteManifest.ReleaseTime = DateTime.UtcNow;

            var ignoreFiles = (from n in remoteManifest.Files
                               where n.Ignore
                               select n).ToArray();

            var ignoreMap = ignoreFiles.ToDictionary(k => k.Path, k => "", Duplicati.Library.Utility.Utility.ClientFilenameStringComparer);

            remoteManifest.MD5 = null;
            remoteManifest.SHA256 = null;
            remoteManifest.Files = null;
            remoteManifest.UncompressedSize = 0;

            var localManifest = remoteManifest.Clone();
            localManifest.RemoteURLS = null;

            inputfolder = Util.AppendDirSeparator(inputfolder);
            var baselen = inputfolder.Length;
            var dirsep = Util.DirectorySeparatorString;

            ignoreMap.Add(UPDATE_MANIFEST_FILENAME, "");

            var md5 = System.Security.Cryptography.MD5.Create();
            var sha256 = System.Security.Cryptography.SHA256.Create();

            Func<Stream, string> computeStreamMD5 = (stream) =>
            {
                md5.Initialize();
                return Convert.ToBase64String(md5.ComputeHash(stream));
            };

            Func<Stream, string> computeStreamSHA256 = (stream) =>
            {
                sha256.Initialize();
                return Convert.ToBase64String(sha256.ComputeHash(stream));
            };

            Func<string, string> computeMD5 = (path) =>
            {
                using (Stream fs = System.IO.File.OpenRead(path))
                    return computeStreamMD5(fs);
            };

            Func<string, string> computeSHA256 = (path) =>
            {
                using (Stream fs = System.IO.File.OpenRead(path))
                    return computeStreamSHA256(fs);
            };

            // Build a zip
            using (var archive_temp_file = new Duplicati.Library.Utility.TempFile())
            {
                using (var archive_temp = System.IO.File.Open(archive_temp_file, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    using (ICompression zipfile = new Duplicati.Library.Compression.FileArchiveZip(archive_temp, ArchiveMode.Write, new Dictionary<string, string>()))
                    {
                        Func<string, string, bool> addToArchive = (path, relpath) =>
                        {
                            if (ignoreMap.ContainsKey(relpath))
                                return false;

                            if (path.EndsWith(dirsep, StringComparison.Ordinal))
                                return true;

                            using (var source = System.IO.File.OpenRead(path))
                            using (var target = zipfile.CreateFile(relpath,
                                               Duplicati.Library.Interface.CompressionHint.Compressible,
                                               System.IO.File.GetLastAccessTimeUtc(path)))
                            {
                                source.CopyTo(target);
                                remoteManifest.UncompressedSize += source.Length;
                            }

                            return true;
                        };

                        // Build the update manifest
                        localManifest.Files =
                    (from fse in Duplicati.Library.Utility.Utility.EnumerateFileSystemEntries(inputfolder)
                     let relpath = fse.Substring(baselen)
                     where addToArchive(fse, relpath)
                     select new FileEntry()
                     {
                         Path = relpath,
                         LastWriteTime = System.IO.File.GetLastAccessTimeUtc(fse),
                         MD5 = fse.EndsWith(dirsep, StringComparison.Ordinal) ? null : computeMD5(fse),
                         SHA256 = fse.EndsWith(dirsep, StringComparison.Ordinal) ? null : computeSHA256(fse)
                     })
                    .Union(ignoreFiles).ToArray();

                        // Write a signed manifest with the files

                        using (var ms = new System.IO.MemoryStream())
                        using (var sw = new System.IO.StreamWriter(ms))
                        {
                            new Newtonsoft.Json.JsonSerializer().Serialize(sw, localManifest);
                            sw.Flush();

                            using (var ms2 = new System.IO.MemoryStream())
                            {
                                SignatureReadingStream.CreateSignedStream(ms, ms2, key);
                                ms2.Position = 0;
                                using (var sigfile = zipfile.CreateFile(UPDATE_MANIFEST_FILENAME,
                                    Duplicati.Library.Interface.CompressionHint.Compressible,
                                    DateTime.UtcNow))
                                    ms2.CopyTo(sigfile);

                            }
                        }
                    }

                    remoteManifest.CompressedSize = archive_temp.Length;

                    archive_temp.Position = 0;
                    remoteManifest.MD5 = computeStreamMD5(archive_temp);

                    archive_temp.Position = 0;
                    remoteManifest.SHA256 = computeStreamSHA256(archive_temp);
                }
                System.IO.File.Move(archive_temp_file, System.IO.Path.Combine(outputfolder, "package.zip"));
            }

            // Write a signed manifest for upload

            using (var tf = new Duplicati.Library.Utility.TempFile())
            {
                using (var ms = new System.IO.MemoryStream())
                using (var sw = new System.IO.StreamWriter(ms))
                {
                    new Newtonsoft.Json.JsonSerializer().Serialize(sw, remoteManifest);
                    sw.Flush();

                    using (var fs = System.IO.File.Create(tf))
                        SignatureReadingStream.CreateSignedStream(ms, fs, key);
                }

                System.IO.File.Move(tf, System.IO.Path.Combine(outputfolder, UPDATE_MANIFEST_FILENAME));
            }

        }

        private static void WrapWithUpdater(AutoUpdateStrategy defaultstrategy, Action wrappedFunction)
        {
            string optstr = Environment.GetEnvironmentVariable(string.Format(UPDATE_STRATEGY_ENVNAME_TEMPLATE, APPNAME));
            AutoUpdateStrategy strategy;
            if (string.IsNullOrWhiteSpace(optstr) || !Enum.TryParse(optstr, true, out strategy))
                strategy = defaultstrategy;

            System.Threading.Thread backgroundChecker = null;
            UpdateInfo updateDetected = null;
            bool updateInstalled = false;

            bool checkForUpdate;
            bool downloadUpdate;
            bool runAfter;
            bool runDuring;
            bool runBefore;


            switch (strategy)
            {
                case AutoUpdateStrategy.CheckBefore:
                case AutoUpdateStrategy.CheckDuring:
                case AutoUpdateStrategy.CheckAfter:
                    checkForUpdate = true;
                    downloadUpdate = false;
                    break;

                case AutoUpdateStrategy.InstallBefore:
                case AutoUpdateStrategy.InstallDuring:
                case AutoUpdateStrategy.InstallAfter:
                    checkForUpdate = true;
                    downloadUpdate = true;
                    break;

                default:
                    checkForUpdate = false;
                    downloadUpdate = false;
                    break;
            }

            switch (strategy)
            {
                case AutoUpdateStrategy.CheckBefore:
                case AutoUpdateStrategy.InstallBefore:
                    runBefore = true;
                    runDuring = false;
                    runAfter = false;
                    break;

                case AutoUpdateStrategy.CheckAfter:
                case AutoUpdateStrategy.InstallAfter:
                    runBefore = false;
                    runDuring = false;
                    runAfter = true;
                    break;

                case AutoUpdateStrategy.CheckDuring:
                case AutoUpdateStrategy.InstallDuring:
                    runBefore = false;
                    runDuring = true;
                    runAfter = false;
                    break;

                default:
                    runBefore = false;
                    runDuring = false;
                    runAfter = false;
                    break;
            }

            if (checkForUpdate)
            {
                backgroundChecker = new System.Threading.Thread(() =>
                {
                    // Don't run "during" if the task is short
                    if (runDuring)
                        System.Threading.Thread.Sleep(TimeSpan.FromSeconds(10));

                    updateDetected = CheckForUpdate();
                    if (updateDetected != null && downloadUpdate)
                    {
                        if (!runDuring)
                            Console.WriteLine("Update to {0} detected, installing...", updateDetected.Displayname);
                        updateInstalled = DownloadAndUnpackUpdate(updateDetected);
                    }
                });

                backgroundChecker.IsBackground = true;
                backgroundChecker.Name = "BackgroundUpdateChecker";

                if (!runAfter)
                    backgroundChecker.Start();

                if (runBefore)
                {
                    Console.WriteLine("Checking for update ...");
                    backgroundChecker.Join();

                    if (downloadUpdate)
                    {
                        if (updateInstalled)
                            Console.WriteLine("Install succeeded, running updated version");
                        else
                            Console.WriteLine("Install or download failed, using current version");
                    }
                    else if (updateDetected != null)
                    {
                        Console.WriteLine("Update \"{0}\" detected", updateDetected.Displayname);
                    }

                    backgroundChecker = null;
                }
            }

            wrappedFunction();

            if (backgroundChecker != null && runAfter)
            {
                Console.WriteLine("Checking for update ...");

                backgroundChecker.Start();
                backgroundChecker.Join();
            }

            if (backgroundChecker != null && updateDetected != null)
            {
                if (backgroundChecker.IsAlive)
                {
                    Console.WriteLine("Waiting for update \"{0}\" to complete", updateDetected.Displayname);
                    backgroundChecker.Join();
                }

                if (downloadUpdate)
                {
                    if (updateInstalled)
                        Console.WriteLine("Install succeeded, running updated version on next launch");
                    else
                        Console.WriteLine("Install or download failed, using current version on next launch");
                }
                else
                {
                    Console.WriteLine("Update \"{0}\" detected", updateDetected.Displayname);
                }
            }
        }

        private static int RunMethod(System.Reflection.MethodInfo method, string[] args)
        {
            try
            {
                var n = method.Invoke(null, new object[] { args });
                if (method.ReturnType == typeof(int))
                    return (int)n;

                return 0;
            }
            catch (System.Reflection.TargetInvocationException tex)
            {
                try
                {
                    Console.WriteLine("Crash! {0}{1}", Environment.NewLine, tex);
                }
                catch
                {
                }

                try
                {
                    var report_file = System.IO.Path.Combine(
                        string.IsNullOrEmpty(INSTALLDIR) ? Library.Utility.TempFolder.SystemTempPath : INSTALLDIR,
                        string.Format("{0}-crashlog.txt", AutoUpdateSettings.AppName)
                     );

                    System.IO.File.WriteAllText(report_file, tex.ToString());
                }
                catch
                {
                }

                if (tex.InnerException != null)
                {
                    // Unwrap exceptions for nicer display.  The ExceptionDispatchInfo class allows us to
                    // rethrow an exception without changing the stack trace.
                    ExceptionDispatchInfo.Capture(tex.InnerException).Throw();
                }
                
                throw;
            }
        }

        private static KeyValuePair<string, UpdateInfo> GetBestUpdateVersion(bool forcecheck = false)
        {
            if (forcecheck)
                m_hasUpdateInstalled = null;

            // Check if there are updates installed, otherwise use current
            KeyValuePair<string, UpdateInfo> best = new KeyValuePair<string, UpdateInfo>(AppDomain.CurrentDomain.SetupInformation.ApplicationBase, SelfVersion);
            if (HasUpdateInstalled)
                best = m_hasUpdateInstalled.Value;

            if (INSTALLDIR != null && System.IO.File.Exists(System.IO.Path.Combine(INSTALLDIR, CURRENT_FILE)))
            {
                try
                {
                    var current = System.IO.File.ReadAllText(System.IO.Path.Combine(INSTALLDIR, CURRENT_FILE)).Trim();
                    if (!string.IsNullOrWhiteSpace(current))
                    {
                        var targetfolder = System.IO.Path.Combine(INSTALLDIR, current);
                        var currentmanifest = ReadInstalledManifest(targetfolder);
                        if (currentmanifest != null && TryParseVersion(currentmanifest.Version) > TryParseVersion(best.Value.Version) && VerifyUnpackedFolder(targetfolder, currentmanifest))
                            best = new KeyValuePair<string, UpdateInfo>(targetfolder, currentmanifest);
                    }
                }
                catch (Exception ex)
                {
                    if (OnError != null)
                        OnError(ex);
                }
            }

            return best;
        }

        public static bool IsRunningInUpdateEnvironment => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(string.Format(BASEINSTALLDIR_ENVNAME_TEMPLATE, APPNAME)));

        public static int RunFromMostRecent(System.Reflection.MethodInfo method, string[] cmdargs, AutoUpdateStrategy defaultstrategy = AutoUpdateStrategy.CheckDuring)
        {
            if (Library.Utility.Utility.ParseBool(Environment.GetEnvironmentVariable("AUTOUPDATER_USE_APPDOMAIN"), false))
                return RunFromMostRecentAppDomain(method, cmdargs, defaultstrategy);

            return RunFromMostRecentSpawn(method, cmdargs, defaultstrategy);
        }

        public static int RunFromMostRecentSpawn(System.Reflection.MethodInfo method, string[] cmdargs, AutoUpdateStrategy defaultstrategy = AutoUpdateStrategy.CheckDuring)
        {
            // If the update is disabled, go straight in
            if (DISABLE_UPDATE_DOMAIN)
                return RunMethod(method, cmdargs);

            // If we are not the primary entry, just execute
            if (IsRunningInUpdateEnvironment)
            {
                // For some reason this does not work
                //if (Platform.IsClientWindows)
                    //Duplicati.Library.Utility.Win32.AttachConsole(Duplicati.Library.Utility.Win32.ATTACH_PARENT_PROCESS);

                int r = 0;
                WrapWithUpdater(defaultstrategy, () => {
                    r = RunMethod(method, cmdargs);
                });

                return r;
            }

            var app = Environment.GetCommandLineArgs().First();
            var args = Library.Utility.Utility.WrapAsCommandLine(Environment.GetCommandLineArgs().Skip(1), false);

            if (!Path.IsPathRooted(app))
                app = Path.Combine(InstalledBaseDir, app);

            var executable = Path.GetFileName(app);

            while (true)
            {
                var best = GetBestUpdateVersion(true);
                var folder = best.Key;

                var pi = new System.Diagnostics.ProcessStartInfo(Path.Combine(folder, executable), args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    ErrorDialog = false,
                };
                pi.EnvironmentVariables.Clear();

                var cur = Environment.GetEnvironmentVariables();
                foreach (var e in cur.Keys)
                    if (e is string s)
                        pi.EnvironmentVariables[s] = cur[s] as string;

                pi.EnvironmentVariables[string.Format(BASEINSTALLDIR_ENVNAME_TEMPLATE, APPNAME)] = InstalledBaseDir;
                pi.EnvironmentVariables["LOCALIZATION_FOLDER"] = InstalledBaseDir;

                // On Windows, we manually redirect the streams
                if (Platform.IsClientWindows)
                {
                    pi.RedirectStandardError = true;
                    pi.RedirectStandardInput = true;
                    pi.RedirectStandardOutput = true;
                }

                var proc = System.Diagnostics.Process.Start(pi);
                Task tasks = null;
                if (Platform.IsClientWindows)
                {
                    // On Windows, we manually redirect the streams
                    tasks = Task.WhenAll(
                        // This does some unwanted buffering that breaks things
                        //Console.OpenStandardInput().CopyToAsync(proc.StandardInput.BaseStream),
                        Task.Run(async () => {
                            var stdin = new StreamReader(Console.OpenStandardInput());
                            var line = string.Empty;
                            while ((line = await stdin.ReadLineAsync().ConfigureAwait(false)) != null)
                                await proc.StandardInput.WriteLineAsync(line);
                        }),
                        proc.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput()),
                        proc.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError())
                    );
                }

                proc.WaitForExit();
                if (tasks != null)
                    tasks.Wait(1000);

                if (proc.ExitCode != MAGIC_EXIT_CODE)
                    return proc.ExitCode;
            }

        }

        public static int RunFromMostRecentAppDomain(System.Reflection.MethodInfo method, string[] cmdargs, AutoUpdateStrategy defaultstrategy = AutoUpdateStrategy.CheckDuring)
        {
            // If the update is disabled, go straight in
            if (DISABLE_UPDATE_DOMAIN)
                return RunMethod(method, cmdargs);

            // If we are not the primary domain, just execute
            if (!AppDomain.CurrentDomain.IsDefaultAppDomain())
            {
                int r = 0;
                WrapWithUpdater(defaultstrategy, () => {
                    r = RunMethod(method, cmdargs);
                });

                return r;
            }

            // If we are a re-launch, wait briefly for the other process to exit
            var sleepmarker = System.Environment.GetEnvironmentVariable(string.Format(SLEEP_ENVNAME_TEMPLATE, APPNAME));
            if (!string.IsNullOrWhiteSpace(sleepmarker))
            {
                System.Environment.SetEnvironmentVariable(string.Format(SLEEP_ENVNAME_TEMPLATE, APPNAME), null);
                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(10));
            }

            // Check if there are updates installed, otherwise use current
            KeyValuePair<string, UpdateInfo> best = new KeyValuePair<string, UpdateInfo>(AppDomain.CurrentDomain.SetupInformation.ApplicationBase, SelfVersion);
            if (HasUpdateInstalled)
                best = m_hasUpdateInstalled.Value;

            if (INSTALLDIR != null && System.IO.File.Exists(System.IO.Path.Combine(INSTALLDIR, CURRENT_FILE)))
            {
                try
                {
                    var current = System.IO.File.ReadAllText(System.IO.Path.Combine(INSTALLDIR, CURRENT_FILE)).Trim();
                    if (!string.IsNullOrWhiteSpace(current))
                    {
                        var targetfolder = System.IO.Path.Combine(INSTALLDIR, current);
                        var currentmanifest = ReadInstalledManifest(targetfolder);
                        if (currentmanifest != null && TryParseVersion(currentmanifest.Version) > TryParseVersion(best.Value.Version) && VerifyUnpackedFolder(targetfolder, currentmanifest))
                            best = new KeyValuePair<string, UpdateInfo>(targetfolder, currentmanifest);
                    }
                }
                catch (Exception ex)
                {
                    if (OnError != null)
                        OnError(ex);
                }
            }

            Environment.SetEnvironmentVariable(string.Format(BASEINSTALLDIR_ENVNAME_TEMPLATE, APPNAME), InstalledBaseDir);

            var folder = best.Key;

            // Basic idea with the loop is that the running AppDomain can use
            // RUN_UPDATED_ENVNAME_TEMPLATE to signal that a new version is ready
            // when the caller exits, the new update is executed
            //
            // This allows more or less seamless updates
            //

            int result = 0;
            while (!string.IsNullOrWhiteSpace(folder) && System.IO.Directory.Exists(folder))
            {
                var prevfolder = folder;
                // Create the new domain
                var domain = AppDomain.CreateDomain(
                                 "UpdateDomain",
                                 null,
                                 folder,
                                 "",
                                 false
                             );

                domain.SetData("LOCALIZATION_FOLDER", InstalledBaseDir);

                result = domain.ExecuteAssemblyByName(method.DeclaringType.Assembly.GetName().Name, cmdargs);

                folder = (string)domain.GetData(RUN_UPDATED_FOLDER_PATH);

                try { AppDomain.Unload(domain); }
                catch (Exception ex)
                {
                    Console.WriteLine("Appdomain unload error: {0}", ex);
                }

                if (!string.IsNullOrWhiteSpace(folder))
                {
                    if (!VerifyUnpackedFolder(folder))
                        folder = prevfolder; //Go back and run the previous version
                    else if (RequiresRespawn)
                    {
                        // We have a valid update, and the current instance is terminated.
                        // But due to external libraries, we need to re-spawn the original process

                        try
                        {
                            var app = Environment.GetCommandLineArgs().First();
                            var args = Library.Utility.Utility.WrapAsCommandLine(Environment.GetCommandLineArgs().Skip(1), false);

                            if (!System.IO.Path.IsPathRooted(app))
                                app = System.IO.Path.Combine(InstalledBaseDir, app);


                            // Re-launch but give the OS a little time to fully unload all open handles, etc.
                            var si = new System.Diagnostics.ProcessStartInfo(app, args);
                            si.UseShellExecute = false;
                            si.EnvironmentVariables.Add(string.Format(SLEEP_ENVNAME_TEMPLATE, APPNAME), "1");

                            System.Diagnostics.Process.Start(si);

                            return 0;
                        }
                        catch (Exception ex)
                        {
                            if (OnError != null)
                                OnError(ex);
                            folder = prevfolder;
                        }
                    }
                }
            }

            return result;
        }

    }
}

