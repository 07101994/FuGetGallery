using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http;
using System.IO.Compression;
using System.Xml.Linq;
using Mono.Cecil;
using System.Threading;

namespace FuGetGallery
{
    public class PackageData
    {
        public string Id { get; set; } = "";
        public string IndexId { get; set; } = "";
        public string Version { get; set; } = "";

        public string Authors { get; set; } = "";
        public string Owners { get; set; } = "";
        public string AuthorsOrOwners => string.IsNullOrEmpty (Authors) ? Owners : Authors;
        public string Description { get; set; } = "";
        public string ProjectUrl { get; set; } = "";
        public string IconUrl { get; set; } = "";

        public string DownloadUrl { get; set; } = "";
        public long SizeInBytes { get; set; }
        public ZipArchive Archive { get; set; }
        public List<PackageTargetFramework> TargetFrameworks { get; set; } = new List<PackageTargetFramework> ();
        public Exception Error { get; set; }

        static readonly PackageDataCache cache = new PackageDataCache ();


        public static Task<PackageData> GetAsync (object inputId, object inputVersion) => GetAsync (inputId, inputVersion, CancellationToken.None);

        public static async Task<PackageData> GetAsync (object inputId, object inputVersion, CancellationToken token)
        {
            var cleanId = (inputId ?? "").ToString().Trim().ToLowerInvariant();

            var versions = await PackageVersions.GetAsync (inputId, token).ConfigureAwait (false);
            var version = versions.GetVersion (inputVersion);

            return await cache.GetAsync (versions.LowerId, version.Version, token).ConfigureAwait (false);
        }

        public PackageTargetFramework FindClosestTargetFramework (object inputTargetFramework)
        {
            var moniker = (inputTargetFramework ?? "").ToString().Trim().ToLowerInvariant();
            
            var tf = TargetFrameworks.FirstOrDefault (x => x.Moniker == moniker);
            if (tf != null) return tf;
            
            tf = TargetFrameworks.LastOrDefault (x => x.Moniker.StartsWith("netstandard2"));
            if (tf != null) return tf;

            tf = TargetFrameworks.LastOrDefault (x => x.Moniker.StartsWith("netstandard"));
            if (tf != null) return tf;
            
            if (tf == null)
                tf = TargetFrameworks.FirstOrDefault ();
            return tf;
        }

        public PackageTargetFramework FindExactTargetFramework (string moniker)
        {
            return TargetFrameworks.FirstOrDefault (x => x.Moniker == moniker);
        }

        void Read (MemoryStream bytes)
        {
            SizeInBytes = bytes.Length;
            Archive = new ZipArchive (bytes, ZipArchiveMode.Read);
            TargetFrameworks.Clear ();
            ZipArchiveEntry nuspecEntry = null;
            foreach (var e in Archive.Entries.OrderBy (x => x.FullName)) {
                var n = e.FullName;
                var isBuild = n.StartsWith ("build/");
                var isLib = n.StartsWith ("lib/");
                if ((isBuild || isLib) && (n.EndsWith(".dll", StringComparison.InvariantCultureIgnoreCase) ||
                                           n.EndsWith(".exe", StringComparison.InvariantCultureIgnoreCase) ||
                                           n.EndsWith(".xml", StringComparison.InvariantCultureIgnoreCase) ||
                                           n.EndsWith(".targets", StringComparison.InvariantCultureIgnoreCase))) {
                    var parts = n.Split ('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3) {
                        var tfm = Uri.UnescapeDataString (parts[1].Trim ().ToLowerInvariant ());
                        var tf = TargetFrameworks.FirstOrDefault (x => x.Moniker == tfm);
                        if (tf == null) {
                            tf = new PackageTargetFramework (Id.ToLowerInvariant ()) {
                                Moniker = tfm,
                            };
                            TargetFrameworks.Add (tf);
                        }
                        if (n.EndsWith(".targets", StringComparison.InvariantCultureIgnoreCase)) {
                        }
                        else if (n.EndsWith(".xml", StringComparison.InvariantCultureIgnoreCase)) {
                            var docs = new PackageAssemblyXmlDocs (e);
                            if (string.IsNullOrEmpty (docs.Error)) {
                                // System.Console.WriteLine(docs.AssemblyName);
                                tf.AssemblyXmlDocs[docs.AssemblyName] = docs;
                            }
                        }
                        else if (isBuild) {
                            tf.BuildAssemblies.Add (new PackageAssembly (e, tf.AssemblyResolver));
                        }
                        else {
                            tf.Assemblies.Add (new PackageAssembly (e, tf.AssemblyResolver));
                        }
                    }
                }
                else if (n.EndsWith (".nuspec", StringComparison.InvariantCultureIgnoreCase)) {
                    nuspecEntry = e;
                }
            }
            if (nuspecEntry != null) {
                ReadNuspec (nuspecEntry);
            }
            TargetFrameworks.Sort ((a,b) => a.Moniker.CompareTo(b.Moniker));
        }

        void ReadNuspec (ZipArchiveEntry entry)
        {
            using (var stream = entry.Open ()) {
                var xdoc = XDocument.Load (stream);
                var ns = xdoc.Root.Name.Namespace;
                var meta = xdoc.Root.Element(ns + "metadata");
                if (meta == null) {
                    throw new Exception ("Failed to find metadata");
                }
                string GetS (string name, string def = "") {
                    try { return meta.Element(ns + name).Value.Trim(); }
                    catch { return def; }
                }
                Id = GetS ("id", IndexId);
                Authors = GetS ("authors");
                Owners = GetS ("owners");
                ProjectUrl = GetS ("projectUrl");
                IconUrl = GetS ("iconUrl");
                Description = GetS ("description");
                var deps = meta.Element(ns + "dependencies");
                if (deps != null) {
                    // System.Console.WriteLine(deps);
                    foreach (var de in deps.Elements()) {
                        if (de.Name.LocalName == "group") {
                            var tfa = de.Attribute("targetFramework");
                            if (tfa != null) {
                                var tfName = tfa.Value;
                                var tf = FindExactTargetFramework (TargetFrameworkNameToMoniker (tfName));
                                if (tf != null) {
                                    foreach (var ge in de.Elements(ns + "dependency")) {
                                        var dep = new PackageDependency (ge);
                                        tf.AddDependency (dep);
                                    }
                                }
                            }
                        }
                        else if (de.Name.LocalName == "dependency") {
                            var dep = new PackageDependency (de);
                            foreach (var tf in TargetFrameworks) {
                                tf.AddDependency (dep);
                            }
                        }
                    }
                }
            }
        }

        static string TargetFrameworkNameToMoniker (string name)
        {
            var r = name.ToLowerInvariant ();
            if (r[0] == '.')
                r = r.Substring(1);
            if (r.StartsWith ("netframework"))
                r = "net" + r.Substring (12).Replace(".", "");
            if (r.StartsWith ("windowsphoneapp"))
                r = "wpa" + r.Substring (15).Replace(".0", "").Replace(".", "");
            if (r.StartsWith ("windowsphone"))
                r = "wp" + r.Substring (12).Replace(".", "");
            if (r.StartsWith ("windows"))
                r = "win" + r.Substring (7).Replace(".0", "").Replace(".", "");
            if (r.StartsWith ("xamarin."))
                r = "xamarin." + r.Substring (8).Replace(".", "");
            if (!r.StartsWith ("uap"))
                r = r.Replace ("0.0", "");
            if (r.StartsWith ("netportable")) {
                var d = r.IndexOf('-');
                var s = r.Substring (d + 1);
                var i = s;
                switch (s) {
                    case "profile7":
                        return "netstandard1.1";
                    case "profile31":
                        return "netstandard1.0";
                    case "profile32":
                        return "netstandard1.2";
                    case "profile44":
                        return "netstandard1.2";
                    case "profile49":
                        return "netstandard1.0";
                    case "profile78":
                        return "netstandard1.0";
                    case "profile84":
                        return "netstandard1.0";
                    case "profile111":
                        return "netstandard1.1";
                    case "profile151":
                        return "netstandard1.2";
                    case "profile157":
                        return "netstandard1.0";
                    case "profile259":
                        return "netstandard1.0";
                    case "profile328":
                        i = "net40+sl5+win8+wp8+wpa81";
                        break;
                }
                r = "portable-" + i;
            }
            return r;
        }

        class PackageDataCache : DataCache<string, string, PackageData>
        {
            public PackageDataCache () : base (TimeSpan.FromDays (365)) { }
            readonly HttpClient httpClient = new HttpClient ();
            protected override async Task<PackageData> GetValueAsync(string id, string version, CancellationToken token)
            {
                var package = new PackageData {
                    Id = id,
                    IndexId = id,
                    Version = version,
                    SizeInBytes = 0,
                    DownloadUrl = $"https://www.nuget.org/api/v2/package/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(version)}",
                };
                token.ThrowIfCancellationRequested();
                try {
                    // System.Console.WriteLine($"DOWNLOADING {token.IsCancellationRequested} {package.DownloadUrl}");
                    var r = await httpClient.GetAsync (package.DownloadUrl, token).ConfigureAwait (false);
                    var data = new MemoryStream ();
                    using (var s = await r.Content.ReadAsStreamAsync().ConfigureAwait(false)) {
                        await s.CopyToAsync (data, 16*1024, token).ConfigureAwait(false);
                    }
                    data.Position = 0;
                    await Task.Run (() => package.Read (data)).ConfigureAwait (false);
                }
                catch (Exception ex) {
                    package.Error = ex;
                }

                return package;
            }
        }
    }
}
