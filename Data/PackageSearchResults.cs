using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mono.Cecil;

namespace FuGetGallery
{
    public class PackageSearchResults
    {
        public string Query { get; set; } = "";
        public Exception Error { get; set; }
        public List<PackageSearchResult> Results { get; set; } = new List<PackageSearchResult> ();

        public PackageSearchResults ()
        {
        }

        static readonly ResultsCache cache = new ResultsCache ();

        public static Task<PackageSearchResults> GetAsync (string inputId)
        {
            var cleanId = (inputId ?? "").ToString().Trim().ToLowerInvariant();

            return cache.GetAsync (cleanId);
            
            // "https://api-v2v3search-0.nuget.org/query?q=%s" (Uri.EscapeDataString (searchTerm))
            // return Task.FromResult<PackageSearchResults> (null);

        }

        void Read (string json)
        {
            var j = Newtonsoft.Json.Linq.JObject.Parse (json);
            var d = (Newtonsoft.Json.Linq.JArray)j["data"];
            foreach (var x in d) {
                var r = new PackageSearchResult {
                    PackageId = (string)x["id"],
                    Version = (string)x["version"],
                    Description = (string)x["description"],
                    IconUrl = (string)x["iconUrl"],
                    TotalDownloads = (int)x["totalDownloads"],
                    Authors = string.Join (", ", x["authors"]),
                };
                // System.Console.WriteLine(r);
                Results.Add (r);
            }
            // Results.Sort ((a, b) => -a.TotalDownloads.CompareTo (b.TotalDownloads));
        }

        class ResultsCache : DataCache<string, PackageSearchResults>
        {
            public ResultsCache () : base (TimeSpan.FromMinutes (15)) { }
            readonly HttpClient httpClient = new HttpClient ();
            protected override async Task<PackageSearchResults> GetValueAsync (string q, CancellationToken token)
            {
                var results = new PackageSearchResults {
                    Query = q,
                };
                try {
                    // System.Console.WriteLine($"DOWNLOADING {package.DownloadUrl}");
                    var queryUrl = "https://api-v2v3search-0.nuget.org/query?q=" + Uri.EscapeDataString (q);
                    var data = await httpClient.GetStringAsync (queryUrl).ConfigureAwait (false);
                    results.Read (data);
                }
                catch (Exception ex) {
                    results.Error = ex;
                }

                return results;
            }
        }
    }

    public class PackageSearchResult
    {
        public string PackageId { get; set; } = "";
        public string Version { get; set; } = "";
        public string Description { get; set; } = "";
        public string IconUrl { get; set; } = "";
        public int TotalDownloads { get; set; } = 0;
        public string Authors { get; set; } = "";

        public override string ToString() => PackageId;
    }
}
