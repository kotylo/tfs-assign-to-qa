using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AssignToQa.Data;
using AssignToQa.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace AssignToQa.Managers
{
    class TfsCrudManager
    {
        private Logger _logger = LogManager.GetCurrentClassLogger();

        private string _getSetWorkItemUrlFormat;

        private string _urlToRepository;
        private string _baseUrl = ConfigurationManager.AppSettings["baseUrl"].TrimEnd('/');
        private string _baseProjectName = ConfigurationManager.AppSettings["baseProjectName"];

        private string _pullRequestsToTake = ConfigurationManager.AppSettings["pullRequestsToTake"];

        public TfsCrudManager(string repositoryName, string branchName)
        {
            _urlToRepository = $"{_baseUrl}/{_baseProjectName}/_apis/git/repositories/{repositoryName}/pullRequests?targetRefName=refs/heads/{branchName}&api-version=3.0&status=completed";
            Initialize();
        }

        public TfsCrudManager()
        {
            Initialize();
        }

        private void Initialize()
        {
            _getSetWorkItemUrlFormat = _baseUrl + "/_apis/wit/workitems/{0}?api-version=1.0&$expand=relations";
        }

        public string UpdateWorkItem(UpdateWorkItemRequest request)
        {
            try
            {
                using (WebClient client = new WebClient())
                {
                    client.UseDefaultCredentials = true;
                    client.Headers.Add("Content-Type", "application/json-patch+json");
                    client.Encoding = Encoding.UTF8;

                    var operations = new List<object>();
                    string opValue = null;

                    if (request.State != Constants.Tfs.State.None)
                    {
                        opValue = "replace";
                        operations.Add(new
                        {
                            op = opValue,
                            path = "/fields/System.State",
                            value = request.State.GetDescription()
                        });
                    }
                    if (request.Title != null)
                    {
                        opValue = request.ExistingFields["System.Title"] == null ? "add" : "replace";
                        operations.Add(new
                        {
                            op = opValue,
                            path = "/fields/System.Title",
                            value = request.Title
                        });
                    }
                    if (request.Tags != null)
                    {
                        opValue = request.ExistingFields["System.Tags"] == null ? "add" : "replace";
                        operations.Add(new
                        {
                            op = opValue,
                            path = "/fields/System.Tags",
                            value = request.Tags
                        });
                    }
                    if (request.AssignedTo != null)
                    {
                        opValue = request.ExistingFields["System.AssignedTo"] == null ? "add" : "replace";
                        operations.Add(
                            new
                            {
                                op = opValue,
                                path = "/fields/System.AssignedTo",
                                value = request.AssignedTo
                            }
                        );
                    }
                    var serializedData = JsonConvert.SerializeObject(operations);
                    var url = string.Format(_getSetWorkItemUrlFormat, request.WorkItemId);
                    _logger.Trace($"Sending followind data to url [PATCH][{url}]: {serializedData}");
                    var responseBody = client.UploadString(url, "PATCH", serializedData);
                    return responseBody;
                }
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Error, $"Error updating work item #{request.WorkItemId}: {ex}");
            }
            return null;
        }

        public string GetPullRequests()
        {
            if (string.IsNullOrEmpty(_urlToRepository))
            {
                throw new Exception("Repository URL is empty.");
            }

            try
            {
                using (WebClient client = new WebClient())
                {
                    client.UseDefaultCredentials = true;
                    client.Encoding = Encoding.UTF8;
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

        public string GetUrl(string url)
        {
            try
            {
                using (WebClient client = new WebClient())
                {
                    client.UseDefaultCredentials = true;
                    client.Encoding = Encoding.UTF8;
                    var responseBody = client.DownloadString(url);
                    return responseBody;
                }
            }
            catch (WebException ex)
            {
                var stream = ex.Response.GetResponseStream();
                using (var reader = new StreamReader(stream))
                {
                    var content = reader.ReadToEnd();
                    dynamic contentObject = JsonConvert.DeserializeObject(content);
                    string message = contentObject.message;
                    if (message != null)
                    {
                        _logger.Log(LogLevel.Error, $"Error reading: {message}");
                    }
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


    }
}
