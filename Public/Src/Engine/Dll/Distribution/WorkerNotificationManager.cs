// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Engine.Distribution.OpenBond;
using BuildXL.Scheduler;
using BuildXL.Utilities.Configuration;

namespace BuildXL.Engine.Distribution
{
    internal interface IWorkerNotificationManager 
    {
        /// <summary>
        /// Starts the notification manager, which will start to listen
        /// and forwarding events to the orchestrator
        /// </summary>
        void Start(IOrchestratorClient orchestratorClient, EngineSchedule schedule);

        /// <summary>
        /// Report an (error / warning) event
        /// </summary>
        void ReportEventMessage(EventMessage eventMessage);

        /// <summary>
        /// Report a pip result
        /// </summary>
        public void ReportResult(ExtendedPipCompletionData pipCompletion);

        /// <summary>
        /// Stop trying to communicate with the orchestrator and processing messages
        /// </summary>
        public void Cancel();

        /// <summary>
        /// Stop listening for events and wait for any pending messages to be sent
        /// </summary>
        public void Exit();
    }

    /// <summary>
    /// Manages notification sending from worker to orchestrator
    /// </summary>
    internal partial class WorkerNotificationManager : IWorkerNotificationManager
    {
        internal readonly WorkerService WorkerService;
        internal readonly DistributionServices DistributionServices;
        private Scheduler.Scheduler m_scheduler;
        private CancellationTokenSource m_sendCancellationSource;
        private IPipExecutionEnvironment m_environment;
        private volatile bool m_started;

        /// Individual sources for notifications
        private PipResultListener m_pipResultListener;
        private ForwardingEventListener m_forwardingEventListener;
        private readonly BlockingCollection<EventMessage> m_outgoingEvents = new BlockingCollection<EventMessage>();
        private NotifyOrchestratorExecutionLogTarget m_executionLogTarget;
        private readonly MemoryStream m_flushedExecutionLog = new MemoryStream();

        /// Notification sending
        private IOrchestratorClient m_orchestratorClient;
        private Thread m_sendThread;
        private readonly int m_maxMessagesPerBatch = EngineEnvironmentSettings.MaxMessagesPerBatch.Value;

        private int m_xlgBlobSequenceNumber = 0;
        private int m_numBatchesSent = 0;
        private volatile bool m_finishedSendingPipResults;

        // Reusable objects for send thread
        private readonly List<ExtendedPipCompletionData> m_executionResults = new List<ExtendedPipCompletionData>();
        private readonly List<EventMessage> m_eventList = new List<EventMessage>();
        private readonly WorkerNotificationArgs m_notification = new WorkerNotificationArgs();

        internal uint WorkerId => WorkerService.WorkerId;
        
        /// <nodoc/>
        public WorkerNotificationManager(WorkerService workerService, DistributionServices services)
        {
            WorkerService = workerService;
            DistributionServices = services;
        }

        public void Start(IOrchestratorClient orchestratorClient, EngineSchedule schedule)
        {
            Contract.AssertNotNull(orchestratorClient);

            m_scheduler = schedule.Scheduler;
            m_environment = m_scheduler;
            m_orchestratorClient = orchestratorClient;

            
            m_executionLogTarget = new NotifyOrchestratorExecutionLogTarget(this, m_environment.Context, m_scheduler.PipGraph.GraphId, m_scheduler.PipGraph.MaxAbsolutePathIndex);
            m_scheduler.AddExecutionLogTarget(m_executionLogTarget);

            m_forwardingEventListener = new ForwardingEventListener(this);

            m_pipResultListener = new PipResultListener(this, schedule, m_environment);
            m_sendCancellationSource = new CancellationTokenSource();
            m_sendThread = new Thread(() => SendNotifications(m_sendCancellationSource.Token));
            m_sendThread.Start();

            m_started = true;
        }

        public void Exit()
        {
            if (!m_started)
            {
                return;
            }

            // Stop listening to events
            m_pipResultListener.Cancel();
            m_forwardingEventListener.Cancel();

            // The execution log target can be null if the worker failed to attach to the orchestrator
            if (m_executionLogTarget != null)
            {
                m_executionLogTarget.Deactivate();

                // Remove the notify orchestrator target to ensure no further events are sent to it.
                // Otherwise, the events that are sent to a disposed target would cause crashes.
                m_scheduler.RemoveExecutionLogTarget(m_executionLogTarget);
            }

            if (m_sendThread.IsAlive)
            {
                // Wait for the queues to drain
                m_sendThread.Join();
            }

            m_executionLogTarget?.Dispose();
            m_forwardingEventListener?.Dispose();
            m_sendCancellationSource.Cancel();
        }

        /// <inheritdoc/>
        public void Cancel()
        {
            if (m_started)
            {
                m_executionLogTarget?.Deactivate();
                m_pipResultListener.Cancel();
                m_forwardingEventListener.Cancel();
                m_sendCancellationSource.Cancel();
            }
        }

        public void ReportResult(ExtendedPipCompletionData pipCompletion)
        {
            Contract.Assert(m_started);
            m_pipResultListener.ReportResult(pipCompletion);
        }

        internal void FlushExecutionLog(MemoryStream listenerStream)
        {
            // Because the execution log target is filling its own 
            // buffer in a different thread, we have to copy its current buffer
            // to our own buffer to have a static blob to send with the notification
            // TODO: A better way of doing this without copying bytes. 
            // Maybe add a "Pause" operation to the binary logger so it blocks its thread
            // while we read/send the buffer.
            listenerStream.WriteTo(m_flushedExecutionLog);
            if (m_finishedSendingPipResults && !m_sendCancellationSource.IsCancellationRequested && m_flushedExecutionLog.Length > 0)
            {
                // A final flush of the execution log may come after all the pip results are sent
                // In that case, we send the blob individually:
                m_orchestratorClient.NotifyAsync(new WorkerNotificationArgs()
                {
                    ExecutionLogBlobSequenceNumber = m_xlgBlobSequenceNumber++,
                    ExecutionLogData = new ArraySegment<byte>(m_flushedExecutionLog.GetBuffer(), 0, (int)m_flushedExecutionLog.Length)
                },
                null, 
                m_sendCancellationSource.Token).Wait();

                m_flushedExecutionLog.SetLength(0);
            }
        }

        /// <nodoc/>
        public void ReportEventMessage(EventMessage eventMessage)
        {
            Contract.Assert(m_started);

            // TODO: Associate eventMessage to pip id and delay queuing
            if (m_sendCancellationSource.IsCancellationRequested)
            {
                // We are not sending messages anymore
                return;
            }

            try
            {
                m_outgoingEvents.Add(eventMessage);
            }
            catch (InvalidOperationException)
            {
                Contract.Assert(m_outgoingEvents.IsAddingCompleted);
                // m_outgoingEvents is marked as complete: this means we are shutting down,
                // don't try to send more events to the orchestrator.
                //
                // Events can occur after the shutdown in communications is started: in
                // builds with FireForgetMaterializeOutputs we may still be executing output
                // materialization while closing down communications with the orchestrator 
                // (which called Exit on the worker already after sending the MaterializeOutput requests).
            }
        }

        private void SendNotifications(CancellationToken cancellationToken)
        {
            ExtendedPipCompletionData firstItem;
            while (!m_pipResultListener.ReadyToSendResultList.IsCompleted && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Sending of notifications is driven by pip results - block until we have a new result to send
                    firstItem = m_pipResultListener.ReadyToSendResultList.Take(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Sending was cancelled 
                    break;
                }
                catch (InvalidOperationException)
                {
                    // The results queue was completed
                    // But we still may have events / xlg to send
                    firstItem = null;
                }

                // 1. Pip results
                m_executionResults.Clear();
                if (firstItem != null)
                {
                    m_executionResults.Add(firstItem);

                    while (m_executionResults.Count < m_maxMessagesPerBatch && m_pipResultListener.ReadyToSendResultList.TryTake(out var item))
                    {
                        m_executionResults.Add(item);
                    }
                }

                // 2. Forwarded events 
                m_eventList.Clear();
                while (m_outgoingEvents.TryTake(out var item))
                {
                    m_eventList.Add(item);
                }

                // 3. Pending execution log events
                using (DistributionServices.Counters.StartStopwatch(DistributionCounter.WorkerFlushExecutionLogDuration))
                {
                    // Flush execution log to m_pendingExecutionLog
                    m_executionLogTarget.FlushAsync().Wait();
                }

                if (m_executionResults.Count == 0 && m_eventList.Count == 0 && m_flushedExecutionLog.Length == 0)
                {
                    // Nothing to send. This can potentially happen while exiting.
                    continue;
                }

                // Send notification
                m_notification.WorkerId = WorkerId;
                m_notification.CompletedPips = m_executionResults.Select(p => p.SerializedData).ToList();
                m_notification.ForwardedEvents = m_eventList;
                
                if (m_flushedExecutionLog.Length > 0)
                {
                    m_notification.ExecutionLogBlobSequenceNumber = m_xlgBlobSequenceNumber++;
                    m_notification.ExecutionLogData = new ArraySegment<byte>(m_flushedExecutionLog.GetBuffer(), 0, (int)m_flushedExecutionLog.Length);
                } 
                else
                {
                    m_notification.ExecutionLogBlobSequenceNumber = 0;
                    m_notification.ExecutionLogData = new ArraySegment<byte>();
                }

                using (DistributionServices.Counters.StartStopwatch(DistributionCounter.SendNotificationDuration))
                {
                    var callResult = m_orchestratorClient.NotifyAsync(m_notification, 
                        m_executionResults.Select(a => a.SemiStableHash).ToList(),
                        cancellationToken).GetAwaiter().GetResult();

                    if (callResult.Succeeded)
                    {
                        foreach (var result in m_executionResults)
                        {
                            WorkerService.PipReportedToOrchestrator(result);
                        }
                        
                        m_numBatchesSent++;
                    }
                    else if (!cancellationToken.IsCancellationRequested)
                    {
                        // Fire-forget exit call with failure.
                        // If we fail to send notification to orchestrator and we were not cancelled, the worker should fail.
                        m_executionLogTarget.Deactivate();
                        WorkerService.ExitAsync(failure: "Notify event failed to send to orchestrator", isUnexpected: true);
                        break;
                    }
                }

                m_flushedExecutionLog.SetLength(0);
            }

            m_finishedSendingPipResults = true;
            m_outgoingEvents.CompleteAdding(); 
            DistributionServices.Counters.AddToCounter(DistributionCounter.BuildResultBatchesSentToOrchestrator, m_numBatchesSent);
        }
    }
}