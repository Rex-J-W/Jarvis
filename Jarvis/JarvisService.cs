﻿using Jarvis.Behaviors;
using Jarvis.API;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Timers;
using Newtonsoft.Json;
using System.Threading.Tasks;
using System.Text;
using Microsoft.ML;

namespace Jarvis
{
    /// <summary>
    /// State of the Jarvis Windows service.
    /// (Used Internally)
    /// </summary>
    public enum ServiceState
    {
        SERVICE_STOPPED = 0x00000001,
        SERVICE_START_PENDING = 0x00000002,
        SERVICE_STOP_PENDING = 0x00000003,
        SERVICE_RUNNING = 0x00000004,
        SERVICE_CONTINUE_PENDING = 0x00000005,
        SERVICE_PAUSE_PENDING = 0x00000006,
        SERVICE_PAUSED = 0x00000007,
    }

    /// <summary>
    /// (Used Internally)
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatus
    {
        public int dwServiceType;
        public ServiceState dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
    };

    /// <summary>
    /// The Jarvis Windows service class.  Contains critical functions.
    /// </summary>
    public partial class Jarvis : ServiceBase
    {
        private const int updateMs = 100, logMs = 90000, webRequestMs = 2000;
        private Timer updateTimer, logTimer, webRequestTimer;
        private ServiceState state;

        private string requestsJson, responsesJson;
        private List<JarvisRequest> requests;
        private readonly List<JarvisRequest> unfilledRequests;
        private List<JarvisResponse> responses;

        private static Jarvis singleton;
        private static readonly HttpClient client = new HttpClient();
        private static readonly string requestUrl = "https://jarvislinker.azurewebsites.net/api/JarvisRequests",
            responseUrl = "https://jarvislinker.azurewebsites.net/api/JarvisResponses";

        private readonly List<IUpdate> updateBehaviors;
        private readonly List<IWebUpdate> webBehaviors;
        private readonly List<IStart> startBehaviors;
        private readonly List<IStop> stopBehaviors;

        private readonly List<object> hotLoadedBehaviors;

        /// <summary>
        /// The MLContext used for all Jarvis ML.Net machine learning functions.
        /// </summary>
        public static readonly MLContext mlContext = new MLContext(100);

        /// <summary>
        /// Create a new Jarvis service instance.
        /// </summary>
        public Jarvis()
        {
            InitializeComponent();
            singleton = this;
            eventLog = new EventLog();
            requestsJson = string.Empty;
            responsesJson = string.Empty;
            if (!EventLog.SourceExists("JarvisEventSource"))
                EventLog.CreateEventSource("JarvisEventSource", "JarvisLog");
            eventLog.Source = "JarvisEventSource";
            eventLog.Log = "JarvisLog";
            eventLog.Clear();
            eventLog.WriteEntry("Initialized Service", EventLogEntryType.Information, 0);
            state = ServiceState.SERVICE_STOPPED;

            string behaviorsText = string.Empty;
            updateBehaviors = new List<IUpdate>();
            webBehaviors = new List<IWebUpdate>();
            startBehaviors = new List<IStart>();
            stopBehaviors = new List<IStop>();
            Type[] types = Assembly.GetExecutingAssembly().GetTypes();
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i].IsClass && types[i].GetInterface(nameof(IBehaviorBase)) != null)
                {
                    object inst = Activator.CreateInstance(types[i]);
                    if (types[i].GetInterface(nameof(IUpdate)) != null)
                    {
                        updateBehaviors.Add(inst as IUpdate);
                        behaviorsText += "\n" + types[i].Name + " (Update)";
                    }
                    if (types[i].GetInterface(nameof(IWebUpdate)) != null)
                    {
                        webBehaviors.Add(inst as IWebUpdate);
                        behaviorsText += "\n" + types[i].Name + " (WebUpdate)";
                    }
                    if (types[i].GetInterface(nameof(IStart)) != null)
                    {
                        startBehaviors.Add(inst as IStart);
                        behaviorsText += "\n" + types[i].Name + " (Start)";
                    }
                    if (types[i].GetInterface(nameof(IStop)) != null)
                    {
                        stopBehaviors.Add(inst as IStop);
                        behaviorsText += "\n" + types[i].Name + " (Stop)";
                    }
                }
            }
            updateBehaviors.Sort(CompareBehaviors);
            webBehaviors.Sort(CompareBehaviors);
            startBehaviors.Sort(CompareBehaviors);
            stopBehaviors.Sort(CompareBehaviors);
            eventLog.WriteEntry("Initialized Behaviors: " + behaviorsText, EventLogEntryType.Information, 6);

            requests = new List<JarvisRequest>();
            unfilledRequests = new List<JarvisRequest>();
            responses = new List<JarvisResponse>();

            hotLoadedBehaviors = new List<object>();
        }

        private int CompareBehaviors(IBehaviorBase x, IBehaviorBase y)
        {
            if (x.Priority < y.Priority) return -1;
            else if (x.Priority > y.Priority) return 1;
            return 0;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus serviceStatus);

        protected override void OnStart(string[] args)
        {
            ServiceStatus serviceStatus = new ServiceStatus
            {
                dwCurrentState = ServiceState.SERVICE_START_PENDING,
                dwWaitHint = 100000
            };
            SetServiceStatus(ServiceHandle, ref serviceStatus);
            state = ServiceState.SERVICE_START_PENDING;

            try
            {
                for (int i = 0; i < startBehaviors.Count; i++)
                    if (startBehaviors[i].Enabled)
                        startBehaviors[i].Start();
            }
            catch (Exception e)
            {
                eventLog.WriteEntry("Error On Start: " + e.Message, EventLogEntryType.Error, 9);
            }

            updateTimer = new Timer(updateMs);
            logTimer = new Timer(logMs);
            webRequestTimer = new Timer(webRequestMs);
            updateTimer.Start();
            logTimer.Start();
            webRequestTimer.Start();
            updateTimer.Elapsed += UpdateTimer_Elapsed;
            logTimer.Elapsed += LogTimer_Elapsed;
            webRequestTimer.Elapsed += WebRequestTimer_Elapsed;
            eventLog.WriteEntry("Started", EventLogEntryType.Information, 1);

            state = ServiceState.SERVICE_RUNNING;
            serviceStatus.dwCurrentState = ServiceState.SERVICE_RUNNING;
            SetServiceStatus(ServiceHandle, ref serviceStatus);
        }

        protected override void OnStop()
        {
            ServiceStatus serviceStatus = new ServiceStatus
            {
                dwCurrentState = ServiceState.SERVICE_STOP_PENDING,
                dwWaitHint = 100000
            };
            SetServiceStatus(ServiceHandle, ref serviceStatus);
            state = ServiceState.SERVICE_STOP_PENDING;

            updateTimer.Dispose();
            logTimer.Dispose();
            webRequestTimer.Dispose();

            try
            {
                for (int i = 0; i < stopBehaviors.Count; i++)
                    if (stopBehaviors[i].Enabled)
                        stopBehaviors[i].Stop();
            }
            catch (Exception e)
            {
                eventLog.WriteEntry("Error On Stop: " + e.Message, EventLogEntryType.Error, 10);
            }
            eventLog.WriteEntry("Stopped", EventLogEntryType.Information, 2);

            state = ServiceState.SERVICE_STOPPED;
            serviceStatus.dwCurrentState = ServiceState.SERVICE_STOPPED;
            SetServiceStatus(ServiceHandle, ref serviceStatus);
        }

        protected override void OnPause()
        {
            ServiceStatus serviceStatus = new ServiceStatus
            {
                dwCurrentState = ServiceState.SERVICE_PAUSE_PENDING,
                dwWaitHint = 100000
            };
            SetServiceStatus(ServiceHandle, ref serviceStatus);
            state = ServiceState.SERVICE_PAUSE_PENDING;

            updateTimer.Stop();
            logTimer.Stop();
            webRequestTimer.Stop();
            eventLog.WriteEntry("Paused", EventLogEntryType.Information, 3);

            state = ServiceState.SERVICE_PAUSED;
            serviceStatus.dwCurrentState = ServiceState.SERVICE_PAUSED;
            SetServiceStatus(ServiceHandle, ref serviceStatus);
        }

        protected override void OnContinue()
        {
            ServiceStatus serviceStatus = new ServiceStatus
            {
                dwCurrentState = ServiceState.SERVICE_CONTINUE_PENDING,
                dwWaitHint = 100000
            };
            SetServiceStatus(ServiceHandle, ref serviceStatus);
            state = ServiceState.SERVICE_CONTINUE_PENDING;

            updateTimer.Start();
            logTimer.Start();
            webRequestTimer.Start();
            eventLog.WriteEntry("Unpaused", EventLogEntryType.Information, 4);

            state = ServiceState.SERVICE_RUNNING;
            serviceStatus.dwCurrentState = ServiceState.SERVICE_RUNNING;
            SetServiceStatus(ServiceHandle, ref serviceStatus);
        }

        private void UpdateTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                for (int i = 0; i < updateBehaviors.Count; i++)
                    if (updateBehaviors[i].Enabled)
                        updateBehaviors[i].Update();
            }
            catch (Exception ex)
            {
                eventLog.WriteEntry("Error On Update: " + ex.Message, EventLogEntryType.Error, 11);
            }
        }

        private async void WebRequestTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            webRequestTimer.Stop();
            HttpResponseMessage requestMsg = await client.GetAsync(requestUrl),
                responseMsg = await client.GetAsync(responseUrl);
            if (requestMsg.IsSuccessStatusCode && responseMsg.IsSuccessStatusCode)
            {
                requestsJson = await requestMsg.Content.ReadAsStringAsync();
                responsesJson = await responseMsg.Content.ReadAsStringAsync();
                requests = JsonConvert.DeserializeObject<List<JarvisRequest>>(requestsJson);
                responses = JsonConvert.DeserializeObject<List<JarvisResponse>>(responsesJson);

                unfilledRequests.Clear();
                HashSet<long> filledRequests = new HashSet<long>();
                for (int i = 0; i < responses.Count; i++)
                    if (!filledRequests.Contains(responses[i].RequestId))
                        filledRequests.Add(responses[i].RequestId);
                for (int i = 0; i < requests.Count; i++)
                    if (!filledRequests.Contains(requests[i].Id))
                        unfilledRequests.Add(requests[i]);
                try
                {
                    for (int i = 0; i < webBehaviors.Count; i++)
                        if (webBehaviors[i].Enabled)
                            webBehaviors[i].WebUpdate();
                }
                catch (Exception ex)
                {
                    eventLog.WriteEntry("Error On WebUpdate: " + ex.Message, EventLogEntryType.Error, 12);
                }
            }
            else eventLog.WriteEntry("Web Update Failed\nCode: " + requestMsg.StatusCode.ToString(), EventLogEntryType.Warning, 7);
            webRequestTimer.Start();
        }

        private void LogTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            string log = "- Status Check -\nState: " + state.ToString() +
                "\nBehaviors: " + (updateBehaviors.Count + webBehaviors.Count + startBehaviors.Count + stopBehaviors.Count) +
                "\n" + requests.Count + " Requests:\n" + requestsJson +
                "\n\n" + responses.Count + " Responses:\n" + responsesJson +
                "\n\n(" + unfilledRequests.Count + " Unfilled Requests)";
            eventLog.WriteEntry(log, EventLogEntryType.Information, 5);
        }

        private async Task<bool> SendResponse(string data, string type, long requestId)
        {
            JarvisResponseDTO dto = new JarvisResponseDTO
            {
                Type = type,
                Data = data,
                RequestId = requestId
            };
            string json = JsonConvert.SerializeObject(dto);
            StringContent jsonContent = new StringContent(json, Encoding.UTF8, "application/json");
            return (await client.PostAsync(responseUrl, jsonContent)).IsSuccessStatusCode;
        }

        /// <summary>
        /// Link to low level Jarvis service functions
        /// </summary>
        public static class Service
        {
            /// <summary>
            /// Logs an event to the Javis event log.
            /// </summary>
            /// <param name="msg">Log message</param>
            /// <param name="type">Type of log message</param>
            public static void Log(string msg, EventLogEntryType type = EventLogEntryType.Information, int code = 8) =>
                singleton.eventLog.WriteEntry(msg, type, code);

            /// <summary>
            /// Forces the Jarvis service to stop.
            /// </summary>
            public static void ForceStop() => singleton.Stop();

            /// <summary>
            /// Gets the JarvisRequest objects from the last API ping that haven't been responded too.
            /// </summary>
            /// <returns>Current JarvisRequest list</returns>
            public static List<JarvisRequest> GetUnfilledRequests() => singleton.unfilledRequests;

            /// <summary>
            /// Trys to send a response for a specified JarvisRequest.
            /// </summary>
            /// <returns>If the reponse was sent successfully</returns>
            public static async Task<bool> TrySendResponse(string data, string type, long requestId)
            {
                bool canSend = false;
                int requestIndex = -1;
                for (int i = 0; i < singleton.unfilledRequests.Count; i++)
                {
                    if (singleton.unfilledRequests[i].Id == requestId)
                    {
                        canSend = true;
                        requestIndex = i;
                        break;
                    }
                }

                if (canSend)
                {
                    bool completed = await singleton.SendResponse(data, type, requestId);
                    if (completed)
                    {
                        singleton.unfilledRequests.RemoveAt(requestIndex);
                        return true;
                    }
                }
                return false;
            }
        }
    }
}
