﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Cassandra;
using KillrVideo.Search.Dtos;
using KillrVideo.Utils;
using RestSharp;
using Newtonsoft.Json;
using System.Net;
using Serilog;
using System.Collections.Generic;
namespace KillrVideo.Search.SearchImpl
{
    /// <summary>
    /// Searches videos using DataStax Enterprise search (Solr integration).
    /// </summary>
    public class DataStaxEnterpriseSearch : ISearchVideos
    {
        private readonly ISession _session;
        private readonly TaskCache<string, PreparedStatement> _statementCache;
        private readonly IRestClient _restClient;

        public DataStaxEnterpriseSearch(ISession session, TaskCache<string, PreparedStatement> statementCache, IRestClient restClient)
        {
            if (session == null) throw new ArgumentNullException("session");
            if (statementCache == null) throw new ArgumentNullException("statementCache");
            _session = session;
            _statementCache = statementCache;
            _restClient = restClient;

        }

        /// <summary>
        /// Gets a page of videos for a search query.
        /// </summary>
        public async Task<VideosForSearchQuery> SearchVideos(SearchVideosQuery searchVideosQuery)
        {
            // Do a Solr query against DSE search to find videos using Solr's ExtendedDisMax query parser. Query the
            // name, tags, and description fields in the videos table giving a boost to matches in the name and tags
            // fields as opposed to the description field
            // More info on ExtendedDisMax: http://wiki.apache.org/solr/ExtendedDisMax
            string solrQuery = "{ \"q\": \"{!edismax qf=\\\"name^2 tags^1 description\\\"}" + searchVideosQuery.Query + "\" }";
            
            PreparedStatement prepared = await _statementCache.NoContext.GetOrAddAsync(
                "SELECT videoid, userid, name, preview_image_location, added_date FROM videos WHERE solr_query=?");

            // The driver's built-in paging feature just works with DSE Search Solr paging which is pretty cool
            IStatement bound = prepared.Bind(solrQuery)
                                       .SetAutoPage(false)
                                       .SetPageSize(searchVideosQuery.PageSize);

            // The initial query won't have a paging state, but subsequent calls should if there are more pages
            if (string.IsNullOrEmpty(searchVideosQuery.PagingState) == false)
                bound.SetPagingState(Convert.FromBase64String(searchVideosQuery.PagingState));

            RowSet rows = await _session.ExecuteAsync(bound).ConfigureAwait(false);
            
            return new VideosForSearchQuery
            {
                Query = searchVideosQuery.Query,
                Videos = rows.Select(MapRowToVideoPreview).ToList(),
                PagingState = rows.PagingState != null && rows.PagingState.Length > 0 ? Convert.ToBase64String(rows.PagingState) : null
            };
        }

        /// <summary>
        /// Gets a list of query suggestions for providing typeahead support.
        /// </summary>
        public async Task<SuggestedQueries> GetQuerySuggestions(GetQuerySuggestions getSuggestions)
        {

            // Set the base URL of the REST client to use the first node in the Cassandra cluster
             string nodeIp = _session.Cluster.AllHosts().First().Address.Address.ToString();
            _restClient.BaseUrl = new Uri(string.Format("http://{0}:8983/solr", nodeIp));

          

            var request = new RestRequest("killrvideo.videos/suggest");
            request.Method = Method.POST;
            request.AddParameter("wt", "json");
            // Requires a build after new names are added, added on a safe side.
            request.AddParameter("spellcheck.build", "true");
            request.AddParameter("spellcheck.q", getSuggestions.Query);
            IRestResponse<SearchSuggestionResult> response = await _restClient.ExecuteTaskAsync<SearchSuggestionResult>(request).ConfigureAwait(false);



            // Check for network/timeout errors
            if (response.ResponseStatus != ResponseStatus.Completed)
            {
                //Logger.Error(response.ErrorException, "Error while querying Solr search suggestions from {host} for {query}", nodeIp, getSuggestions.Query);
                return new SuggestedQueries { Query = getSuggestions.Query, Suggestions = Enumerable.Empty<string>() };
            }

            // Check for HTTP error codes
            if (response.StatusCode != HttpStatusCode.OK)
            {
                //Logger.Error("HTTP status code {code} while querying Solr video suggestions from {host} for {query}", (int)response.StatusCode, nodeIp, getSuggestions.Query);
                return new SuggestedQueries { Query = getSuggestions.Query, Suggestions = Enumerable.Empty<string>() };
            }
            // Success

            // Json embeds another json object within an array.
            // Ensures we receive data
            if (response.Data.Spellcheck.Suggestions.Count >= 2)
            {
                // Deserialize the embedded object
                var suggestions = JsonConvert.DeserializeObject<SearchSpellcheckSuggestions>(response.Data.Spellcheck.Suggestions.Last());
                // Ensure the object deserialized correctly
                if (suggestions.Suggestion != null)
                    return new SuggestedQueries { Query = getSuggestions.Query, Suggestions = suggestions.Suggestion };
            }

            return new SuggestedQueries
            {
                Query = getSuggestions.Query,
                Suggestions = Enumerable.Empty<string>()
            };
        }

        private static VideoPreview MapRowToVideoPreview(Row row)
        {
            return new VideoPreview
            {
                VideoId = row.GetValue<Guid>("videoid"),
                AddedDate = row.GetValue<DateTimeOffset>("added_date"),
                Name = row.GetValue<string>("name"),
                PreviewImageLocation = row.GetValue<string>("preview_image_location"),
                UserId = row.GetValue<Guid>("userid")
            };
        }
    }
}