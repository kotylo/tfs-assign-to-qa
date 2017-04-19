using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using AssignToQa.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace AssignToQa
{
    internal class TfsWorker
    {
        private Logger _logger = LogManager.GetCurrentClassLogger();

        private string _personalAccessToken = ConfigurationManager.AppSettings["pat"];

        private string _baseUrl = ConfigurationManager.AppSettings["baseUrl"].TrimEnd('/');
        private string _baseProjectName = ConfigurationManager.AppSettings["baseProjectName"];
        private string _defaultTester  = ConfigurationManager.AppSettings["defaultTester"];
        private string _defaultDomain = ConfigurationManager.AppSettings["defaultDomain"];

        private List<string> _titlesToSkip =
            ConfigurationManager.AppSettings["titlesToSkip"].Split(new[] {";"}, StringSplitOptions.RemoveEmptyEntries)
                .ToList();

        private List<string> _allowedCreators =
            ConfigurationManager.AppSettings["allowedCreators"].Split(new[] {";"}, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        private Dictionary<string, string> _customTesterNamesMapping = new Dictionary<string, string>();
        
        private string _pullRequestsToTake = ConfigurationManager.AppSettings["pullRequestsToTake"];
        
        private string _getSetWorkItemUrlFormat;
        private string _urlToRepository;

        private int _lastPullRequestsCount;
        private LastChangedWorkItems _lastChangedWorkItems = new LastChangedWorkItems();
        private string _pathToProcessedPullRequests = "processed.txt";
        private string _name;

        public TfsWorker(string repositoryName)
        {
            this._name = repositoryName;
            _urlToRepository = $"{_baseUrl}/{_baseProjectName}/_apis/git/repositories/{repositoryName}/pullRequests?targetRefName=refs/heads/staging&api-version=3.0&status=completed";
            _getSetWorkItemUrlFormat = _baseUrl + "/_apis/wit/workitems/{0}?api-version=1.0&$expand=relations";

            LoadCustomTesterNamesMapping();
        }

        private void LoadCustomTesterNamesMapping()
        {
            var customNameDomains = ConfigurationManager.AppSettings["customTesterNamesMapping"].Split(new [] {";"}, StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach (string nameDomain in customNameDomains)
            {
                var nameDomainPair = nameDomain.Split(':');
                if (nameDomainPair.Length != 2)
                {
                    throw new ArgumentException("Please separate customTesterNamesMapping parameter with : separator, like name:DOMAIN\\NAME");
                }
                _customTesterNamesMapping.Add(nameDomainPair[0], nameDomainPair[1]);
            }
        }

        public object Name => _name;

        public string GetUrl(string url)
        {
            try
            {
                using (WebClient client = new WebClient())
                {
                    client.UseDefaultCredentials = true;
                    var responseBody = client.DownloadString(url);
                    return responseBody;
                }
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, $"Error reading {url}: {ex}");
            }
            return null;
        }

        public JObject GetWorkItem(string url)
        {
            var workItemIdMatch = Regex.Match(url, @"/(?<id>\d{6})$");
            if (workItemIdMatch.Success)
            {
                var id = int.Parse(workItemIdMatch.Groups["id"].ToString());
                return GetWorkItem(id);
            }
            _logger.Log(LogLevel.Error, $"Can't parse work item ID from this URL: {url}");
            return null;
        }

        public JObject GetWorkItem(int id)
        {
            var url = string.Format(_getSetWorkItemUrlFormat, id);
            var workItemString = GetUrl(url);
            if (workItemString == null)
            {
                return null;
            }
            var workItem = JObject.Parse(workItemString);
            return workItem;
        }

        public string GetPullRequests()
        {
            try
            {
                using (WebClient client = new WebClient())
                {
                    client.UseDefaultCredentials = true;
                    var pullRequestsToTake = $"&$top={_pullRequestsToTake}";
                    if (_pullRequestsToTake == "all")
                    {
                        pullRequestsToTake = string.Empty;
                    }
                    var url = _urlToRepository + pullRequestsToTake;
                    return client.DownloadString(new Uri(url));
                }
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, "Error reading pull requests: " + ex);
            }
            return null;
        }

        [Obsolete("Authentication with PAT didn't work. Don't use this method.")]
        public async void GetProjectsOld()
        {
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(
                        System.Text.ASCIIEncoding.ASCII.GetBytes($":{_personalAccessToken}")));

                using (HttpResponseMessage response = client.GetAsync(_urlToRepository).Result)
                {
                    response.EnsureSuccessStatusCode();
                    string responseBody = await response.Content.ReadAsStringAsync();
                    _logger.Log(LogLevel.Trace, responseBody);
                }
            }
        }

        public async void AutoUpdate()
        {
            var pullRequests = GetPullRequests();
            ParsePullRequests(pullRequests);
        }

        private object ParsePullRequests(string pullRequests)
        {
            if (pullRequests == null)
            {
                _logger.Log(LogLevel.Debug, "No pull requests, can't parse them.");
                return null;
            }

            _lastChangedWorkItems = new LastChangedWorkItems();

            var jObject = JObject.Parse(pullRequests);
            var pullRequestsCount = int.Parse(jObject["count"].ToString());
            if (pullRequestsCount <= 0)
            {
                _logger.Log(LogLevel.Trace, "No pull requests have been found.");
                return null;
            }

            JArray requests = (JArray)jObject["value"];

            bool isAnyPullRequestProcessed = false;
            foreach (var request in requests)
            {
                var creatorName = request["createdBy"]["displayName"];
                var domainName = request["createdBy"]["uniqueName"].ToString();
                var id = request["pullRequestId"].ToString();

                if (IsPullRequestProcessed(id))
                {
                    // Skip processed pull requests
                    continue;
                }

                if (!_allowedCreators.Contains("all") && !_allowedCreators.Contains(domainName))
                {
                    _logger.Log(LogLevel.Debug, $"Skipping pull request #{id} (by {creatorName}), since it's not assigned to allowed creator!");
                    MarkPullRequestAsCompleted(id);
                    continue;
                }

                if (request["mergeStatus"].ToString() != "succeeded")
                {
                    _logger.Log(LogLevel.Debug, $"Pull request #{id} has not been merged, skipping...");
                    MarkPullRequestAsCompleted(id);
                    continue;
                }

                List<int> workItemIds = GetWorkItemsFromRequest(request);
                if (!workItemIds.Any())
                {
                    _logger.Log(LogLevel.Error, $"No workitems were linked in request, skipping...");
                    MarkPullRequestAsCompleted(id);
                    continue;
                }

                _logger.Log(LogLevel.Info, $"\nProcessing #{id} from {creatorName} with workItems {string.Join(", ", workItemIds)}.");

                UpdateWorkItems(workItemIds);

                isAnyPullRequestProcessed = true;
                MarkPullRequestAsCompleted(id);
            }

            if (isAnyPullRequestProcessed)
            {
                _logger.Log(LogLevel.Debug, "Done processing pull requests.");
                _logger.Log(LogLevel.Info, $"Successes: {_lastChangedWorkItems.SuccessCount}, Failures: {_lastChangedWorkItems.FailureCount}");
                Flasher.FlashWindow();
            }

            return null;
        }

        private bool IsPullRequestProcessed(string id)
        {
            if (!File.Exists(_pathToProcessedPullRequests))
            {
                return false;
            }

            var lines = File.ReadLines(_pathToProcessedPullRequests);
            foreach (var line in lines)
            {
                if (line == id)
                {
                    return true;
                }
            }
            return false;
        }

        private void MarkPullRequestAsCompleted(string id)
        {
            File.AppendAllLines(_pathToProcessedPullRequests, new List<string>() { id });
        }

        private List<int> GetWorkItemsFromRequest(JToken request)
        {
            Match workItemsStringMatch = Match.Empty;
            List<int> workItemIds = new List<int>();

            if (request["completionOptions"]?["mergeCommitMessage"] == null)
            {
                var sourceBranchName = request["sourceRefName"].ToString();
                _logger.Log(LogLevel.Debug, 
                    $"No commit message, so let's try to parse workitems from source branch: {sourceBranchName}");
                workItemsStringMatch = Regex.Match(sourceBranchName, @"\/(?<items>\d{6})($|\D+)");
                if (!workItemsStringMatch.Success)
                {
                    throw new Exception($"No workitems were found for this pull request, tried to get them from branch name: {sourceBranchName}");
                }
            }
            else
            {
                var mergeCommitMessage = request["completionOptions"]["mergeCommitMessage"].ToString();
                //request["completionOptions"] == NULL ?!
                workItemsStringMatch = Regex.Match(mergeCommitMessage, @"Related work items:\s*(?<items>.*?)$");
                if (!workItemsStringMatch.Success)
                {
                    _logger.Log(LogLevel.Debug, 
                        $"No workitems text has been found in the merge commit message: {mergeCommitMessage}");
                    return workItemIds;
                }
            }

            var workItemsString = workItemsStringMatch.Groups["items"].Value;
            var workItemsMatches = Regex.Matches(workItemsString, @"(#|)(?<number>\d+)");

            foreach (Match workItemsMatch in workItemsMatches)
            {
                string workItemString = workItemsMatch.Groups["number"].ToString();
                var workItemId = int.Parse(workItemString);
                workItemIds.Add(workItemId);
            }
            return workItemIds;
        }

        private void UpdateWorkItems(List<int> workItemIds)
        {
            foreach (int workItemId in workItemIds)
            {
                _logger.Log(LogLevel.Debug, $"\tGetting #{workItemId}... ");
                var workItem = GetWorkItem(workItemId);
                if (workItem == null)
                {
                    _logger.Log(LogLevel.Error, "ERROR! ");
                    continue;
                }
                _logger.Log(LogLevel.Debug, "Done! ");
                var fields = workItem["fields"];
                var type = fields["System.WorkItemType"].ToString();
                var state = fields["System.State"].ToString();
                var assignedTo = fields["System.AssignedTo"];
                var title = fields["System.Title"].ToString();
                string tags = string.Empty;
                if (fields["System.Tags"] != null)
                {
                    tags = fields["System.Tags"].ToString();
                }

                _logger.Log(LogLevel.Debug, $"Status of this {type} is {state}, assigned to {assignedTo ?? "Unassigned" }, title is {title}... ");

                var matchedSkipTitle =
                    _titlesToSkip.FirstOrDefault(s => title.IndexOf(s, StringComparison.InvariantCultureIgnoreCase) >= 0);
                if (matchedSkipTitle != null)
                {
                    _logger.Log(LogLevel.Debug, $"Skipping, since we have this title in skip list ({matchedSkipTitle})");
                    continue;
                }

                if (type != "Product Backlog Item" && type != "Bug")
                {
                    _logger.Log(LogLevel.Debug, "Skipping not PBI or Bugs");
                    continue;
                }

                if (state == "Done")
                {
                    _logger.Log(LogLevel.Debug, "Skipping DONE item...");
                    continue;
                }

                if (tags.Contains("Ready For Test"))
                {
                    _logger.Log(LogLevel.Debug, "Skipping already marked as 'Ready for Test' item...");
                    continue;
                }

                var foundUser = FindUserToTest(fields, workItem);
                if (foundUser == null)
                {
                    _logger.Log(LogLevel.Debug, $"Assigning it to default tester {_defaultTester}... ");
                    foundUser = _defaultTester;
                }

                _logger.Log(LogLevel.Debug, $"Adding Ready For Test Tag{(foundUser == null ? "" : " and assigning to user " + foundUser)}... ");

                if (!tags.Contains("Ready For Test"))
                {
                    var preChar = string.Empty;
                    if (!string.IsNullOrWhiteSpace(tags))
                    {
                        preChar = "; ";
                    }
                    tags += $"{preChar}Ready For Test";
                }
                string updateWorkItemResult = UpdateWorkItem(workItemId, tags, foundUser, fields);
                if (updateWorkItemResult == null)
                {
                    _logger.Log(LogLevel.Error, "ERROR! ");
                    continue;
                }
                _logger.Log(LogLevel.Debug, "Done!");
            }
        }

        /// <summary>
        /// Finds in Description, Acceptance Criteria or SystemInfo "Should be tested by DOMAIN\USER" and reassigns PBI to this user, if such text
        /// has been found
        /// </summary>
        private string FindUserToTest(JToken fields, JObject workItem)
        {
            var regex = new Regex(@"test(ed|) by[:\-\s]*\s*(|&nbsp;)\s*(?<name>[A-Za-z\\]+)");

            string acceptanceCriteria = string.Empty;
            if (fields["Microsoft.VSTS.Common.AcceptanceCriteria"] != null)
            {
                acceptanceCriteria = fields["Microsoft.VSTS.Common.AcceptanceCriteria"].ToString();
            }

            var match = regex.Match(acceptanceCriteria);
            if (!match.Success)
            {
                string systemInfo = string.Empty;
                if (fields["Microsoft.VSTS.TCM.SystemInfo"] != null)
                {
                    systemInfo = fields["Microsoft.VSTS.TCM.SystemInfo"].ToString();
                }
                match = regex.Match(systemInfo);
            }

            if (!match.Success)
            {
                string description = string.Empty;
                if (fields["System.Description"] != null)
                {
                    description = fields["System.Description"].ToString();
                }
                match = regex.Match(description);
            }
            
            if (match.Success)
            {
                var name = match.Groups["name"].ToString();
                foreach (var customTester in _customTesterNamesMapping)
                {
                    var userName = customTester.Key;
                    var domainName = customTester.Value;

                    if (name.IndexOf(userName, StringComparison.InvariantCultureIgnoreCase) >= 0)
                    {
                        return domainName;
                    }
                }
                
                _logger.Log(LogLevel.Info, $"I've found {name} in the task.");
                if (!name.StartsWith(_defaultDomain))
                {
                    _logger.Log(LogLevel.Info, $"but I'll not do anything with it, since it doesn't start with {_defaultDomain}.");
                    return null;
                }

                _logger.Log(LogLevel.Info, $"I'll try to use it, since it starts with {_defaultDomain}.");
                return name;
            }

            var userFromLinkedItems = FindUserToTestFromLinkedItems(workItem);
            if (userFromLinkedItems != null)
            {
                return userFromLinkedItems;
            }

            return null;
        }

        /// <summary>
        /// Rules:
        /// 1. Take the only not Done item
        /// 2. Only 1 task should be there
        /// 3. Take user from this Task
        /// 4. Otherwise assign to Default Test User
        /// </summary>
        private string FindUserToTestFromLinkedItems(JObject parentWorkItem)
        {
            var relations = parentWorkItem["relations"];
            if (relations == null)
            {
                _logger.Log(LogLevel.Info, $"This workitem doen't have any linked issues, so can't find user to reassign.");
                return null;
            }

            _logger.Log(LogLevel.Info, "Finding test user in related workitems...");
            var assignedList = new List<string>();
            foreach (var relation in (JArray)relations)
            {
                var relationType = relation["rel"].ToString();
                if (relationType == "System.LinkTypes.Hierarchy-Forward")
                {
                    var workItemUrl = relation["url"].ToString();
                    var workItem = GetWorkItem(workItemUrl);
                    if (workItem != null)
                    {
                        var fields = workItem["fields"];
                        var type = fields["System.WorkItemType"].ToString();
                        if (type != "Task")
                        {
                            _logger.Debug($"Skipping non-task items ({type})");
                            continue;
                        }

                        var assignedTo = fields["System.AssignedTo"];
                        if (assignedTo == null)
                        {
                            _logger.Debug($"Skipping Unassigned users");
                            continue;
                        }

                        var state = fields["System.State"].ToString();
                        if (state == "Done")
                        {
                            _logger.Debug($"Skipping Done items");
                            continue;
                        }

                        var assignedToDomainUserMatch = Regex.Match(assignedTo.ToString(), "<(?<name>[^>]+)>");
                        if (assignedToDomainUserMatch.Success)
                        {
                            var assignedToCleaned = assignedToDomainUserMatch.Groups["name"];
                            _logger.Debug($"Added {assignedToCleaned} user to temporary matches...");
                            assignedList.Add(assignedTo.ToString());
                        }
                    }
                }
            }

            if (assignedList.Count == 1)
            {
                var singleUser = assignedList.First();
                _logger.Info($"Found {singleUser} user, probably that's tester...");
                return singleUser;
            }

            if (assignedList.Count > 1)
            {
                _logger.Info($"We've found {assignedList.Count} users. Can't decide which one to take :)");
            }
            
            _logger.Log(LogLevel.Info, "User is not found at all.");
            return null;
        }

        private string UpdateWorkItem(int workItemId, string tags, string assignedTo, JToken fields)
        {
            try
            {
                using (WebClient client = new WebClient())
                {
                    client.UseDefaultCredentials = true;
                    client.Headers.Add("Content-Type", "application/json-patch+json");

                    var operations = new List<object>();
                    var opValue = fields["System.Tags"] == null ? "add" : "replace";
                    operations.Add(new
                    {
                        op = opValue,
                        path = "/fields/System.Tags",
                        value = tags
                    });
                    if (assignedTo != null)
                    {
                        opValue = fields["System.AssignedTo"] == null ? "add" : "replace";
                        operations.Add(
                            new
                            {
                                op = opValue,
                                path = "/fields/System.AssignedTo",
                                value = assignedTo
                            }
                            );
                    }
                    var serializedData = JsonConvert.SerializeObject(operations);
                    var url = string.Format(_getSetWorkItemUrlFormat, workItemId);
                    _logger.Trace($"Sending followind data to url [PATCH][{url}]: {serializedData}");
                    var responseBody = client.UploadString(url, "PATCH", serializedData);
                    _lastChangedWorkItems.SuccessCount++;
                    return responseBody;
                }
            }
            catch (Exception ex)
            {
                _lastChangedWorkItems.FailureCount++;
                _logger.Log(LogLevel.Error, $"Error updating work item #{workItemId}: {ex}");
            }
            return null;
        }
    }
}