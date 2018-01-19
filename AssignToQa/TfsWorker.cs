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
using AssignToQa.Helpers;
using AssignToQa.Managers;

namespace AssignToQa
{
    internal class TfsWorker
    {
        private Logger _logger = LogManager.GetCurrentClassLogger();

        private string _personalAccessToken = ConfigurationManager.AppSettings["pat"];

        private string _defaultTester  = ConfigurationManager.AppSettings["defaultTester"];
        private string _defaultDomain = ConfigurationManager.AppSettings["defaultDomain"];
        private string _readyForTestTag = ConfigurationManager.AppSettings["readyForTestTag"];
        private string _branchToNotifyInComments = ConfigurationManager.AppSettings["branchToNotifyInComments"];
        private string _autoTfsCommentPrefix = ConfigurationManager.AppSettings["autoTfsCommentPrefix"];
        private string _nonTestableTag = ConfigurationManager.AppSettings["nonTestableTag"];

        private List<string> _titlesToSkip =
            ConfigurationManager.AppSettings["titlesToSkip"].Split(new[] {";"}, StringSplitOptions.RemoveEmptyEntries)
                .ToList();

        private List<string> _allowedCreators =
            ConfigurationManager.AppSettings["allowedCreators"].Split(new[] {";"}, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        private Dictionary<string, string> _customTesterNamesMapping = new Dictionary<string, string>();

        private int _lastPullRequestsCount;
        private LastChangedWorkItems _lastChangedWorkItems = new LastChangedWorkItems();
        private string _pathToProcessedPullRequests = "processed.txt";
        private string _name;

        private TfsCrudManager _tfsCrudManager;

        public TfsWorker(string repositoryName, string branchName)
        {
            _tfsCrudManager = new TfsCrudManager(repositoryName, branchName);

            this._name = repositoryName;

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

        //[Obsolete("Authentication with PAT didn't work. Don't use this method.")]
        //public async void GetProjectsOld()
        //{
        //    using (HttpClient client = new HttpClient())
        //    {
        //        client.DefaultRequestHeaders.Accept.Add(
        //            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        //        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
        //            Convert.ToBase64String(
        //                System.Text.ASCIIEncoding.ASCII.GetBytes($":{_personalAccessToken}")));

        //        using (HttpResponseMessage response = client.GetAsync(_urlToRepository).Result)
        //        {
        //            response.EnsureSuccessStatusCode();
        //            string responseBody = await response.Content.ReadAsStringAsync();
        //            _logger.Log(LogLevel.Trace, responseBody);
        //        }
        //    }
        //}

        public async void AutoUpdate()
        {
            var pullRequests = _tfsCrudManager.GetPullRequests();
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
                    _logger.Log(LogLevel.Warn, $"No workitems were found for this pull request, tried to get them from branch name: {sourceBranchName}, but failed!");
                    return new List<int>();
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
                var workItem = _tfsCrudManager.GetWorkItem(workItemId);
                if (workItem == null)
                {
                    _logger.Log(LogLevel.Error, "ERROR! ");
                    _lastChangedWorkItems.FailureCount++;
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

                _logger.Log(LogLevel.Debug, $"Status of this {type} is {state}, assigned to {assignedTo ?? "Unassigned" }, title is {title}");

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

                if (state == Constants.Tfs.State.Done.GetDescription())
                {
                    _logger.Log(LogLevel.Debug, "Skipping DONE item...");
                    continue;
                }

                if (state == Constants.Tfs.State.Removed.GetDescription())
                {
                    _logger.Log(LogLevel.Debug, "Skipping REMOVED item...");
                    continue;
                }

                if (tags.Contains(_readyForTestTag))
                {
                    _logger.Log(LogLevel.Debug, $"Skipping already marked as '{_readyForTestTag}' item...");
                    continue;
                }

                string foundUser = null;
                if (!tags.Contains(_nonTestableTag))
                {
                    // Only find tester when it's a Testable item
                    try
                    {
                        foundUser = FindUserToTest(fields, workItem);
                        if (foundUser == null)
                        {
                            _logger.Log(LogLevel.Debug, $"Assigning it to default tester {_defaultTester}... ");
                            foundUser = _defaultTester;
                        }
                    }
                    catch (DeveloperHasNotFinishedTaskException)
                    {
                        _logger.Log(LogLevel.Info,
                            $"There is a non-Done child Development task, Skipping this workItem.");
                        continue;
                    }
                }

                if (tags.Contains(_nonTestableTag))
                {
                    if (GetChildTasks(workItem)
                        .Any(o => o["fields"]["System.State"].ToString() == Constants.Tfs.State.InProgress.GetDescription() ||
                                  o["fields"]["System.State"].ToString() == Constants.Tfs.State.ToDo.GetDescription()))
                    {
                        _logger.Log(LogLevel.Debug, $"Skipping setting Done for '{_nonTestableTag}' item... Because there are tasks In Progress or To Do.");
                        continue;
                    }

                    _logger.Log(LogLevel.Debug, $"Setting Done for '{_nonTestableTag}' item... Because all tasks are done.");
                    var result = UpdateWorkItem(new UpdateWorkItemRequest()
                    {
                        AssignedTo = foundUser,
                        ExistingFields = fields,
                        Tags = tags,
                        WorkItemId = workItemId,
                        State = Constants.Tfs.State.Done
                    });
                    if (result == null)
                    {
                        _logger.Log(LogLevel.Error, "ERROR! ");
                        _lastChangedWorkItems.FailureCount++;
                        continue;
                    }
                    _logger.Log(LogLevel.Debug, "Done!");
                    continue;
                }

                _logger.Log(LogLevel.Debug, $"Adding Ready For Test Tag{(foundUser == null ? "" : " and assigning to user " + foundUser)}... ");

                tags = TfsHelper.AddTag(tags, _readyForTestTag);

                var comment = GenerateCommentIfNeeded();

                string updateWorkItemResult = UpdateWorkItem(new UpdateWorkItemRequest()
                {
                    AssignedTo = foundUser,
                    ExistingFields = fields,
                    Tags = tags,
                    WorkItemId = workItemId,
                    Comment = comment
                });
                if (updateWorkItemResult == null)
                {
                    _logger.Log(LogLevel.Error, "ERROR! ");
                    _lastChangedWorkItems.FailureCount++;
                    continue;
                }
                _logger.Log(LogLevel.Debug, "Done!");
            }
        }

        private string GenerateCommentIfNeeded()
        {
            if (string.Equals(_branchToNotifyInComments, _tfsCrudManager.BranchName, StringComparison.OrdinalIgnoreCase))
            {
                return $"{_autoTfsCommentPrefix} This has been fixed in <b>production</b> branch just now";
            }
            return null;
        }

        private IEnumerable<JObject> GetChildTasks(JObject parentWorkItem)
        {
            _logger.Log(LogLevel.Info, "Getting child tasks...");

            var relations = parentWorkItem["relations"];
            if (relations == null)
            {
                _logger.Log(LogLevel.Info, $"This workitem doen't have any linked issues.");
                yield return null;
            }
            
            foreach (var relation in (JArray) relations)
            {
                var relationType = relation["rel"].ToString();
                if (relationType == "System.LinkTypes.Hierarchy-Forward")
                {
                    var workItemUrl = relation["url"].ToString();
                    var workItem = _tfsCrudManager.GetWorkItem(workItemUrl);
                    if (workItem != null)
                    {
                        var fields = workItem["fields"];
                        var type = fields["System.WorkItemType"].ToString();
                        if (type != "Task")
                        {
                            _logger.Debug($"Skipping non-task items ({type})");
                            continue;
                        }

                        yield return workItem;
                    }
                }
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
                    var workItem = _tfsCrudManager.GetWorkItem(workItemUrl);
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
                        var assignedToStr = assignedTo.ToString();
                        var assignedToFriendlyName = TfsHelper.GetUserNameFromString(assignedToStr, false);

                        Func<string> itemDescriptionFunc = () =>
                        {
                            return $"{assignedToFriendlyName} | {fields["System.Title"]}";
                        };

                        var state = fields["System.State"].ToString();
                        if (state == Constants.Tfs.State.Done.GetDescription())
                        {
                            _logger.Debug($"Skipping Done items [{itemDescriptionFunc()}]");
                            continue;
                        }
                        if (state == Constants.Tfs.State.Removed.GetDescription())
                        {
                            _logger.Debug($"Skipping Removed items [{itemDescriptionFunc()}]");
                            continue;
                        }

                        var assignedToDomainUser = TfsHelper.GetUserNameFromString(assignedToStr);
                        if (assignedToDomainUser != null)
                        {
                            _logger.Debug($"Added user to temporary matches [{itemDescriptionFunc()}]");
                            assignedList.Add(assignedToStr);
                        }

                        var activityType = fields["Microsoft.VSTS.Common.Activity"];
                        if (activityType != null)
                        {
                            if (activityType.ToString() == Constants.Tfs.ActivityNames.DevelopmentActivityName)
                            {
                                throw new DeveloperHasNotFinishedTaskException(assignedToDomainUser);
                            }
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

        private string UpdateWorkItem(UpdateWorkItemRequest request)
        {
            var updateResult = _tfsCrudManager.UpdateWorkItem(request);
            if (updateResult == null)
            {
                _lastChangedWorkItems.FailureCount++;
            }
            else
            {
                _lastChangedWorkItems.SuccessCount++;
            }
            return updateResult;
        }
    }
}