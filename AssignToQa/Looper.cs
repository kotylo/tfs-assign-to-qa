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
                var tfsWorker = new TfsWorker(repository);
                _tfsWorkers.Add(tfsWorker);
            }
            
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

                _logger.Log(LogLevel.Debug, $"Sleeping for {minutesToSleep.Minutes} minutes");
                Console.SetCursorPosition(0, Console.CursorTop - 1);
                Thread.Sleep(minutesToSleep);
            }

            _logger.Log(LogLevel.Debug, "Exiting the app!");
        }
    }
}