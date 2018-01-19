using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using AssignToQa.Data;
using AssignToQa.Helpers;
using AssignToQa.Managers;

namespace AssignToQa.WorkItemsWorker
{
    class WorkItemsWorker
    {
        private string _apiVersion = "api-version=2.2";
        private string _baseUrl = ConfigurationManager.AppSettings["baseUrl"].TrimEnd('/');
        private string _getWiqlQuerryUrl;
        private string _createTaskItemUrl;
        private ILogger _logger = LogManager.GetCurrentClassLogger();

        private List<string> _createTasksUsersList = ConfigurationManager.AppSettings["createTasksUsersList"].Split(new []{";"}, StringSplitOptions.RemoveEmptyEntries).ToList();
        private List<string> _qaTeamUsersList = ConfigurationManager.AppSettings["qaTeamUsersList"].Split(new[] { ";" }, StringSplitOptions.RemoveEmptyEntries).ToList();
        private string _baseProjectName = ConfigurationManager.AppSettings["baseProjectName"];

        private string _defaultTester = ConfigurationManager.AppSettings["defaultTester"];
        private string _nonTestableTag = ConfigurationManager.AppSettings["nonTestableTag"];

        private TfsCrudManager _tfsCrudManager;

        public WorkItemsWorker()
        {
            _tfsCrudManager = new TfsCrudManager();

            _getWiqlQuerryUrl = $"{_baseUrl}/_apis/wit/wiql?{_apiVersion}";
            _createTaskItemUrl = $"{_baseUrl}/{_baseProjectName}/_apis/wit/workitems/$Task?{_apiVersion}";
        }

        public void AutoUpdate()
        {
            foreach (string user in _createTasksUsersList)
            {
                var cleanderUser = user.Trim();
                var userName = cleanderUser;
                var teamTagName = string.Empty;
                var startIndex = userName.IndexOf(":", StringComparison.Ordinal);
                if (startIndex >= 0)
                {
                    userName = userName.Remove(startIndex);
                    teamTagName = cleanderUser.Substring(startIndex + 1).Trim();
                }

                var ids = GetPendingWorkItems(userName);
                if (ids.Any())
                {
                    AddMissingTasksToIds(ids, userName, teamTagName);
                }
            }
        }

        private void AddMissingTasksToIds(List<WorkItem> workItems, string developerUserName, string teamTagName)
        {
            foreach (var item in workItems)
            {
                var currentItemData = GetWorkItem(item);
                if (currentItemData == null)
                {
                    _logger.Log(LogLevel.Error, $"Skipping adding Tasks to workItem {item.Id}, since we can't get data for it!");
                    continue;
                }

                _logger.Info($"Adding missing tasks for #{item.Id} (to {developerUserName}).");

                double effort = 0;
                var effortObject = currentItemData["fields"]["Microsoft.VSTS.Scheduling.Effort"];
                if (effortObject != null)
                {
                    effort = double.Parse(effortObject.ToString());
                }

                string tags = string.Empty;
                if (currentItemData["fields"]["System.Tags"] != null)
                {
                    tags = currentItemData["fields"]["System.Tags"].ToString();
                }

                // Fix title
                var originalTitle = currentItemData["fields"]["System.Title"].ToString();
                _logger.Debug($"Title is {originalTitle}.");
                var title = originalTitle.Replace("  ", " ");
                title = Regex.Replace(title, @"\s*->\s*", " → ");

                var doUpdateOriginalItem = false;
                if (originalTitle != title)
                {
                    _logger.Debug($"Fixing title to {title}.");
                    doUpdateOriginalItem = true;
                }

                // Add custom tags
                if (!tags.Contains(teamTagName))
                {
                    _logger.Debug($"Adding team Tag {teamTagName}.");
                    doUpdateOriginalItem = true;

                    tags = TfsHelper.AddTag(tags, teamTagName);
                }

                if (doUpdateOriginalItem)
                {
                    _tfsCrudManager.UpdateWorkItem(new UpdateWorkItemRequest()
                    {
                        Title = title,
                        WorkItemId = item.Id,
                        Tags = tags,
                        ExistingFields = currentItemData["fields"]
                    });
                }

                var sprint = currentItemData["fields"]["System.IterationPath"].ToString();
                var areaPath = currentItemData["fields"]["System.AreaPath"].ToString();
                var teamProject = currentItemData["fields"]["System.TeamProject"].ToString();

                var createdBy = currentItemData["fields"]["System.CreatedBy"].ToString();
                var cleanedCreatedBy = TfsHelper.GetUserNameFromString(createdBy);
                if (cleanedCreatedBy != null)
                {
                    createdBy = cleanedCreatedBy;
                }

                var devCreateResult = CreateWorkItem("Dev: " + title, effort, Constants.Tfs.ActivityNames.DevelopmentActivityName, sprint, areaPath, teamProject, developerUserName, item);
                if (devCreateResult == null)
                {
                    throw new Exception("Dev Task has not been created!");
                }

                if (tags.Contains(_nonTestableTag))
                {
                    _logger.Info($"Skipping QA task creation, because of {_nonTestableTag} tag!");
                }
                else
                {
                    var qaTaskUser = _defaultTester;
                    if (_qaTeamUsersList.Contains(createdBy))
                    {
                        _logger.Info($"User who created this item is in QA list, so let's assign QA task for him.");
                        qaTaskUser = createdBy;
                    }

                    var qaEffort = 0;
                    var qaCreateResult = CreateWorkItem("QA: " + title, qaEffort, Constants.Tfs.ActivityNames.TestingActivityName, sprint,
                        areaPath, teamProject, qaTaskUser, item);
                    if (qaCreateResult == null)
                    {
                        throw new Exception("QA Task has not been created!");
                    }
                }
                _logger.Info("Enjoy!");
            }
        }

        private JObject GetWorkItem(WorkItem item)
        {
            try
            {
                var response = _tfsCrudManager.GetUrl(item.Url);
                var responseObject = JObject.Parse(response);
                return responseObject;
            }catch(Exception ex)
            {
                _logger.Log(LogLevel.Error, $"Error getting work item: {ex}");
            }
            return null;
        }

        public string CreateWorkItem(string title, double remainingWorkHours, string activityType, string sprintPath, string areaPath, string teamProject, string assignedTo, WorkItem parentWorkItem)
        {
            try
            {
                using (WebClient client = new WebClient())
                {
                    client.UseDefaultCredentials = true;
                    client.Headers.Add("Content-Type", "application/json-patch+json");
                    client.Encoding = Encoding.UTF8;
                    var operations = new List<object>();
                    operations.Add(new
                    {
                        op = "add",
                        path = "/fields/System.Title",
                        value = title
                    });
                    if (remainingWorkHours > 0)
                    {
                        operations.Add(new
                        {
                            op = "add",
                            path = "/fields/Microsoft.VSTS.Scheduling.RemainingWork",
                            value = remainingWorkHours
                        });
                        operations.Add(new
                        {
                            op = "add",
                            path = "/fields/Microsoft.VSTS.Scheduling.OriginalEstimate",
                            value = remainingWorkHours
                        });
                    }
                    operations.Add(new
                    {
                        op = "add",
                        path = "/fields/Microsoft.VSTS.Common.Activity",
                        value = activityType
                    });
                    if (assignedTo != null)
                    {
                        operations.Add(new
                        {
                            op = "add",
                            path = "/fields/System.AssignedTo",
                            value = assignedTo
                        });
                    }
                    if (sprintPath != null)
                    {
                        operations.Add(new
                        {
                            op = "add",
                            path = "/fields/System.IterationPath",
                            value = sprintPath
                        });
                        operations.Add(new
                        {
                            op = "add",
                            path = "/fields/System.AreaPath",
                            value = areaPath
                        });
                        operations.Add(new
                        {
                            op = "add",
                            path = "/fields/System.TeamProject",
                            value = teamProject
                        });
                    }
                    operations.Add(new
                    {
                        op = "add",
                        path = "/relations/-",
                        value = new
                        {
                            rel = "System.LinkTypes.Hierarchy-Reverse",
                            url = parentWorkItem.Url,
                            attributes = new
                            {
                                comment = $"Added {activityType} task"
                            }
                        }
                    });
                    var serializedData = JsonConvert.SerializeObject(operations);
                    var url = string.Format(_createTaskItemUrl);
                    _logger.Trace($"Sending followind data to url [PATCH][{url}]: {serializedData}");
                    var responseBody = client.UploadString(url, "PATCH", serializedData);
                    return responseBody;
                }
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, $"Error creating work item: {ex}");
            }
            return null;
        }

        public List<WorkItem> GetPendingWorkItems(string userName)
        {
            var workItemsParsed = new List<WorkItem>();
            string serializedData = string.Empty;
            string url = string.Empty;
            try
            {
                using (WebClient client = new WebClient())
                {
                    client.UseDefaultCredentials = true;
                    client.Encoding = Encoding.UTF8;
                    client.Headers.Add("Content-Type", "application/json");

                    
                    var operations = new
                    {
                        query = $@"
SELECT [System.Id]
FROM WorkItemLinks
WHERE([Source].[System.WorkItemType] = 'Bug' OR[Source].[System.WorkItemType] = 'Product Backlog Item')
And([System.Links.LinkType] = 'Child')
And([Source].[System.State] = 'Committed' OR [Source].[System.State] = 'New' OR [Source].[System.State] = 'Approved')
And([Source].[System.AssignedTo] = '{userName}')
Order By[Changed Date] Desc
mode(DoesNotContain)"
                    };
                    serializedData = JsonConvert.SerializeObject(operations);
                    url = string.Format(_getWiqlQuerryUrl);
                    var responseBody = client.UploadString(url, "POST", serializedData);
                    var response = JObject.Parse(responseBody);
                    JArray workItems = (JArray)response["workItems"];
                    foreach (JToken workItem in workItems)
                    {
                        var workItemId = int.Parse(workItem["id"].ToString());
                        workItemsParsed.Add(new WorkItem()
                        {
                            Id = workItemId,
                            Url = workItem["url"].ToString()
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, $"Error getting work items list: {ex}");
                _logger.Trace($"Error happened while sending followind data to url [POST][{url}]: {serializedData}");
            }
            return workItemsParsed;
        }

        //public void UpdateWorkItem(int workItemId)
        //{
        //    try
        //    {
        //        using (WebClient client = new WebClient())
        //        {
        //            client.UseDefaultCredentials = true;
        //            client.Headers.Add("Content-Type", "application/json");

        //            var operations = new List<object>();
        //            var opValue = fields["System.Tags"] == null ? "add" : "replace";
        //            operations.Add(new
        //            {
        //                op = opValue,
        //                path = "/fields/System.Tags",
        //                value = tags
        //            });
        //            if (assignedTo != null)
        //            {
        //                opValue = fields["System.AssignedTo"] == null ? "add" : "replace";
        //                operations.Add(
        //                    new
        //                    {
        //                        op = opValue,
        //                        path = "/fields/System.AssignedTo",
        //                        value = assignedTo
        //                    }
        //                );
        //            }
        //            var serializedData = JsonConvert.SerializeObject(operations);
        //            var url = string.Format(_getItemFormat, workItemId);
        //            _logger.Trace($"Sending followind data to url [PATCH][{url}]: {serializedData}");
        //            var responseBody = client.UploadString(url, "PATCH", serializedData);
        //            _lastChangedWorkItems.SuccessCount++;
        //            return responseBody;
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        _lastChangedWorkItems.FailureCount++;
        //        _logger.Log(LogLevel.Error, $"Error updating work item #{workItemId}: {ex}");
        //    }
        //    return null;
        //}
    }
}
