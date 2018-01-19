using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Threading;
using NLog;

namespace AssignToQa
{
    internal class Looper
    {
        private Logger _logger = LogManager.GetCurrentClassLogger();
        private List<string> _repositoryList = ConfigurationManager.AppSettings["repositoryList"].Split(';').ToList();
        private double _updateIntervalMinutes = double.Parse(ConfigurationManager.AppSettings["updateIntervalMinutes"]);
        List<TfsWorker> _tfsWorkers = new List<TfsWorker>(); 

        public void Loop()
        {
            var minutesToSleep = TimeSpan.FromMinutes(_updateIntervalMinutes);
            var keepRunning = true;
            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs args)
            {
                _logger.Log(LogLevel.Debug, "Stopping the loop! Wait until program finishes)");
                keepRunning = false;
            };

            foreach (string repository in _repositoryList)
            {
                var branchStartIndex = repository.IndexOf("[", StringComparison.Ordinal);
                if (branchStartIndex >= 0)
                {
                    // There are branches inside, create worker for each of them
                    var repositoryName = repository.Remove(branchStartIndex);
                    var branchesRaw = repository.Substring(branchStartIndex + 1, repository.Length-branchStartIndex-2);
                    var branchNames = branchesRaw.Split(',').Select(x => x.Trim()).ToList();

                    foreach (string branchName in branchNames)
                    {
                        var tfsWorker = new TfsWorker(repositoryName, branchName);
                        _tfsWorkers.Add(tfsWorker);
                    }
                }
                else
                {
                    throw new ArgumentException(
                        "Please specify branch name to monitor in repositoryList property of config, with following syntax: Project[BranchName1, BranchName2]");
                }
            }
            var workItemsWorker = new WorkItemsWorker.WorkItemsWorker();
            
            while (keepRunning)
            {
                try
                {
                    foreach (var tfsWorker in _tfsWorkers)
                    {
                        _logger.Log(LogLevel.Trace, $"Auto-updating {tfsWorker.Name} repository...");
                        tfsWorker.AutoUpdate();
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log(LogLevel.Error, $"Error happened in the LOOP: {ex}");
                }

                workItemsWorker.AutoUpdate();

                ClearCurrentConsoleLine();
                _logger.Log(LogLevel.Debug, $"Sleeping for {minutesToSleep.Minutes} minutes");
                Console.SetCursorPosition(0, Console.CursorTop - 1);
                
                Thread.Sleep(minutesToSleep);
            }

            _logger.Log(LogLevel.Debug, "Exiting the app!");
        }

        public static void ClearCurrentConsoleLine()
        {
            int currentLineCursor = Console.CursorTop;
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write(new string(' ', Console.BufferWidth));
            Console.SetCursorPosition(0, currentLineCursor);
        }
    }
}