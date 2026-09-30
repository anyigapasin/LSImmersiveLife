using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using LemonUI.Elements;
using LemonUI.Menus;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LSImmersiveLife
{
    /// <summary>
    /// Passive runtime inspection for LS Immersive Life.
    ///
    /// Gameplay owners remain responsible for gameplay. Developer Mode only
    /// receives events at the existing log boundary, keeps a bounded in-memory
    /// trace, samples actors that already participated in the active test, and
    /// flushes evidence at explicit boundaries or anomalies.
    /// </summary>
    internal sealed class LSImmersiveDeveloper
    {
        private readonly object _sync = new object();
        private readonly LSIMMERSIVEPATH _paths;
        private readonly LSImmersiveLog _log;
        private readonly LSDeveloperSettings _settings;
        private readonly string _configurationPath;
        private readonly Queue<LSDeveloperTraceEvent> _traceBuffer =
            new Queue<LSDeveloperTraceEvent>();
        private readonly Dictionary<string, string> _lastStates =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, LSDeveloperTaskObservation> _lastTasks =
            new Dictionary<int, LSDeveloperTaskObservation>();
        private long _nextSequence;
        private DateTime _nextSampleUtc;
        private DateTime _nextRuntimeWatchUtc;
        private DateTime _pendingFailureFlushUtc = DateTime.MinValue;
        private string _lastRuntimeWatchSignature = string.Empty;
        private int _selectedActorHandle;
        private LSDeveloperTestSession _activeTest;
        private LSDeveloperTestSession _lastTest;
        private LSDeveloperAnalysisResult _lastAnalysis;
        private bool _shutDown;

        internal NativeMenu Menu { get; private set; }

        internal LSImmersiveDeveloper(LSIMMERSIVEPATH paths, LSImmersiveLog log)
        {
            _paths = paths ?? throw new ArgumentNullException("paths");
            _log = log;
            _configurationPath = _paths.DeveloperXmlPath;
            _settings = LSDeveloperSettings.Load(
                _configurationPath,
                delegate(string message)
                {
                    if (_log != null)
                        _log.Debug("DEVELOPER_CONFIG", message);
                });

            Menu = LSImmersiveMenuFactory.Create(
                "Developer",
                "Bounded runtime inspection and test evidence");
            Menu.NoItemsText = "Developer Mode is disabled in LSDeveloper.xml.";
            BuildMenu();
            LSDeveloperRuntime.Attach(this);

            if (_settings.Enabled && _settings.WriteLifecycleMarkers && _log != null)
            {
                _log.Runtime(
                    "DEVELOPER_READY",
                    "Passive Developer Mode ready; GameplayOwnersUnchanged=true; Config="
                    + _configurationPath
                    + "; Trace=" + _paths.DeveloperTraceLogPath);
            }
        }

        /// <summary>
        /// Runs from the existing universal tick. There is no world scan here:
        /// only actors recorded by the active test are sampled, at the safe
        /// configured interval.
        /// </summary>
        internal void Process()
        {
            if (_shutDown || !_settings.Enabled)
                return;

            DateTime now = DateTime.UtcNow;
            if (_activeTest != null
                && _settings.TraceMode != LSDeveloperTraceMode.Off
                && now >= _nextSampleUtc)
            {
                _nextSampleUtc = now.AddMilliseconds(_settings.SamplingIntervalMilliseconds);
                SampleActiveActors(now);
            }

            if (_pendingFailureFlushUtc != DateTime.MinValue
                && now >= _pendingFailureFlushUtc)
            {
                _pendingFailureFlushUtc = DateTime.MinValue;
                FlushEvidence("post-failure capture");
            }

            if (_settings.AutomaticRuntimeWatch
                && now >= _nextRuntimeWatchUtc)
            {
                _nextRuntimeWatchUtc = now.AddMilliseconds(_settings.SamplingIntervalMilliseconds * 2);
                WatchExistingLogs();
            }
        }

        internal void Shutdown()
        {
            if (_shutDown)
                return;

            try
            {
                if (_activeTest != null)
                    EndTestInternal("Script shutdown", false);

                if (_settings.WriteLifecycleMarkers && _log != null)
                    _log.Runtime("DEVELOPER_STOPPED", "Passive Developer Mode stopped.");
            }
            catch (Exception error)
            {
                LogException("DEVELOPER_SHUTDOWN_FAILED", error);
            }
            finally
            {
                _shutDown = true;
                LSDeveloperRuntime.Detach(this);
            }
        }

        internal void ObserveLog(
            string runtimeSessionId,
            string level,
            string category,
            string message,
            DateTime timestampUtc)
        {
            if (_shutDown || !_settings.Enabled || _settings.TraceMode == LSDeveloperTraceMode.Off
                || (_settings.TraceMode == LSDeveloperTraceMode.Test && _activeTest == null))
                return;
            if (!ShouldObserveLog(category, level))
                return;

            lock (_sync)
            {
                LSDeveloperTraceEvent trace = LSDeveloperTraceEvent.FromLog(
                    runtimeSessionId,
                    level,
                    category,
                    message,
                    timestampUtc);
                AddTraceLocked(trace);
            }
        }

        internal void ObserveExplicitState(
            string runtimeSessionId,
            string owner,
            string category,
            string stateBefore,
            string stateAfter,
            int actorHandle,
            int targetHandle,
            int vehicleHandle,
            string reason,
            DateTime timestampUtc)
        {
            if (_shutDown || !_settings.Enabled || _settings.TraceMode == LSDeveloperTraceMode.Off
                || (_settings.TraceMode == LSDeveloperTraceMode.Test && _activeTest == null))
                return;

            LSDeveloperTraceEvent trace = new LSDeveloperTraceEvent
            {
                TimestampUtc = timestampUtc,
                RuntimeSessionId = runtimeSessionId,
                Owner = owner ?? "Unknown",
                Category = category ?? "STATE_TRANSITION",
                EventKind = "StateTransition",
                ActorHandle = actorHandle,
                TargetHandle = targetHandle,
                VehicleHandle = vehicleHandle,
                StateBefore = stateBefore ?? string.Empty,
                StateAfter = stateAfter ?? string.Empty,
                Reason = reason ?? string.Empty,
                Context = "Explicit central state hook."
            };

            lock (_sync)
                AddTraceLocked(trace);
        }

        internal void ObserveExplicitTask(
            string runtimeSessionId,
            string owner,
            string task,
            int actorHandle,
            int targetHandle,
            int vehicleHandle,
            string reason,
            DateTime timestampUtc)
        {
            if (_shutDown || !_settings.Enabled || _settings.TraceMode == LSDeveloperTraceMode.Off
                || (_settings.TraceMode == LSDeveloperTraceMode.Test && _activeTest == null))
                return;

            LSDeveloperTraceEvent trace = new LSDeveloperTraceEvent
            {
                TimestampUtc = timestampUtc,
                RuntimeSessionId = runtimeSessionId,
                Owner = owner ?? "Unknown",
                Category = "TASK_ISSUED",
                EventKind = "TaskIssued",
                ActorHandle = actorHandle,
                TargetHandle = targetHandle,
                VehicleHandle = vehicleHandle,
                TaskAfter = task ?? string.Empty,
                Reason = reason ?? string.Empty,
                Context = "Explicit task boundary hook."
            };

            lock (_sync)
                AddTraceLocked(trace);
        }

        private void BuildMenu()
        {
            AddHeader("TEST SESSION");

            NativeListItem<string> branch = new NativeListItem<string>(
                "Test Branch",
                "Choose one behavior before starting a controlled test.",
                LSDeveloperBranchCatalog.Names());
            branch.SelectedItem = LSDeveloperBranchCatalog.Names()[0];
            branch.ItemChanged += delegate(object sender, ItemChangedEventArgs<string> args)
            {
                Notify("Developer branch selected: " + args.Object);
            };
            Menu.Add(branch);

            AddAction(
                "Start Test",
                "Start a bounded evidence session. This never starts, stops, or changes gameplay.",
                delegate { StartTest(branch.SelectedItem); });
            AddAction(
                "Capture Checkpoint",
                "Flush the bounded trace around the current controlled test checkpoint.",
                CaptureCheckpoint);
            AddAction(
                "End Test",
                "End the evidence session and preserve the observed timeline for analysis.",
                delegate { EndTestInternal("Player ended test", true); });
            AddAction(
                "Analyze Current Test",
                "Compare the selected branch against the observed event timeline and report the first divergence.",
                AnalyzeCurrentTest);
            AddAction(
                "Compare Checkpoints",
                "Show the event interval between the last two checkpoints.",
                CompareCheckpoints);

            AddHeader("LIVE INSPECTION");
            AddAction(
                "Active Test / Selected Actor",
                "Inspect only the most recently observed actor from the active test.",
                ShowLiveInspection);
            AddAction(
                "Current State / Task / Owner",
                "Show the latest observed logical state, last issued task, owner, target, and vehicle.",
                ShowCurrentActorEvidence);

            AddHeader("BEHAVIOR TRACE");
            AddAction("State Timeline", "Show recent state transitions.", delegate { ShowTimeline("StateTransition"); });
            AddAction("Task Timeline", "Show recent task issue and replacement evidence.", delegate { ShowTimeline("Task"); });
            AddAction("Behavioral Confirmation", "Show the latest low-frequency physical and native task-status samples.", delegate { ShowTimeline("Behavioral"); });
            AddAction("Ownership Timeline", "Show actor ownership changes and conflicts.", delegate { ShowTimeline("Ownership"); });
            AddAction("Dispatch / Backup / Convoy Timeline", "Show the latest supporting-system events.", delegate { ShowTimeline("Systems"); });

            AddHeader("FAILURE ANALYSIS");
            AddAction("First Divergence", "Show the first known or inferred divergence from the last analysis.", ShowFirstDivergence);
            AddAction("Latest Failure", "Show the latest evidence without treating it as the root cause.", ShowLatestFailure);
            AddAction("Task Conflict", "Show incompatible task owners without resolving the conflict.", delegate { ShowTimeline("TaskConflict"); });
            AddAction("Physical Mismatch", "Show logical/physical mismatches observed by selective sampling.", delegate { ShowTimeline("PhysicalMismatch"); });
            AddAction("Behavioral Stall", "Show the first bounded evidence that a selected actor stayed in a stage without the expected next transition.", delegate { ShowTimeline("BehaviorStall"); });

            AddHeader("VALIDATION");
            AddAction("XML / Path / Payload Validation", "Validate the isolated Developer configuration and gameplay data without changing it.", ValidatePayload);
            AddAction("Runtime Log Validation", "Read bounded tails of LSRuntime.log and LSDebug.log and report evidence counts.", ValidateRuntimeLogs);

            AddHeader("SETTINGS");
            NativeListItem<string> traceMode = new NativeListItem<string>(
                "Trace Mode",
                "Off keeps the observer inert; Test buffers evidence for a controlled session; Always keeps the bounded observer active.",
                new[] { "Off", "Test", "Always" });
            traceMode.SelectedItem = _settings.TraceMode.ToString();
            traceMode.ItemChanged += delegate(object sender, ItemChangedEventArgs<string> args)
            {
                _settings.TraceMode = LSDeveloperSettings.ParseTraceMode(args.Object, _settings.TraceMode);
                LogRuntime("DEVELOPER_TRACE_MODE_CHANGED", "TraceMode=" + _settings.TraceMode);
                Notify("Developer trace mode changed to " + _settings.TraceMode + ".");
            };
            Menu.Add(traceMode);

            NativeListItem<bool> anomaly = new NativeListItem<bool>(
                "Automatic Anomaly Detection",
                "Detect serious evidence, task conflicts, and physical mismatches without changing gameplay.",
                new[] { false, true });
            anomaly.SelectedItem = _settings.AutomaticAnomalyDetection;
            anomaly.ItemChanged += delegate(object sender, ItemChangedEventArgs<bool> args)
            {
                _settings.AutomaticAnomalyDetection = args.Object;
                LogRuntime("DEVELOPER_ANOMALY_DETECTION_CHANGED", "Enabled=" + args.Object);
            };
            Menu.Add(anomaly);

            NativeListItem<bool> behavioral = new NativeListItem<bool>(
                "Live Behavioral Confirmation",
                "At the configured sampling interval, read only the selected test actors for movement, weapon, combat, target, and task-status evidence.",
                new[] { false, true });
            behavioral.SelectedItem = _settings.LiveBehavioralConfirmation;
            behavioral.ItemChanged += delegate(object sender, ItemChangedEventArgs<bool> args)
            {
                _settings.LiveBehavioralConfirmation = args.Object;
                LogRuntime("DEVELOPER_BEHAVIORAL_CONFIRMATION_CHANGED", "Enabled=" + args.Object);
                Notify("Live behavioral confirmation " + (args.Object ? "enabled" : "disabled") + ".");
            };
            Menu.Add(behavioral);

            NativeListItem<bool> watch = new NativeListItem<bool>(
                "Automatic Runtime Watch",
                "Read only bounded tails of the two normal logs at a safe interval.",
                new[] { false, true });
            watch.SelectedItem = _settings.AutomaticRuntimeWatch;
            watch.ItemChanged += delegate(object sender, ItemChangedEventArgs<bool> args)
            {
                _settings.AutomaticRuntimeWatch = args.Object;
                _nextRuntimeWatchUtc = DateTime.MinValue;
                LogRuntime("DEVELOPER_RUNTIME_WATCH_CHANGED", "Enabled=" + args.Object);
            };
            Menu.Add(watch);

            AddAction(
                "Save Developer Configuration",
                "Save only LSDeveloper.xml. Police gameplay configuration is not touched.",
                SaveConfiguration);
            AddAction(
                "Reload Developer Configuration",
                "Reload only LSDeveloper.xml. No gameplay system is reloaded or reset.",
                ReloadConfiguration);

            AddHeader("SAFETY BOUNDARY");
            NativeItem boundary = new NativeItem(
                "Observer only: no spawn, delete, teleport, reset, force, or AI override");
            boundary.Enabled = false;
            Menu.Add(boundary);
        }

        private void StartTest(string branchName)
        {
            if (!_settings.Enabled || !_settings.DeveloperMenuEnabled)
            {
                Notify("Developer Mode is disabled in LSDeveloper.xml.");
                return;
            }

            if (_activeTest != null)
                EndTestInternal("Replaced by a newly selected test", true);

            LSDeveloperBranchDefinition branch = LSDeveloperBranchCatalog.Find(branchName);
            if (branch == null)
            {
                Notify("The selected Developer test branch is unavailable.");
                return;
            }

            _activeTest = new LSDeveloperTestSession
            {
                TestId = "DEV-"
                    + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)
                    + "-" + Guid.NewGuid().ToString("N").Substring(0, 6),
                Branch = branch,
                StartedAtUtc = DateTime.UtcNow,
                RuntimeSessionId = _log == null ? string.Empty : _log.SessionId
            };
            lock (_sync)
            {
                _lastStates.Clear();
                _lastTasks.Clear();
            }
            _selectedActorHandle = 0;
            _nextSampleUtc = DateTime.MinValue;
            _pendingFailureFlushUtc = DateTime.MinValue;

            LogRuntime(
                "DEVELOPER_TEST_STARTED",
                "TestId=" + _activeTest.TestId
                + "; Branch=" + branch.Name
                + "; Expected=" + branch.ExpectedBehavior);
            Notify("Developer test started: " + branch.Name + ". Perform one controlled branch only.");
        }

        private void CaptureCheckpoint()
        {
            if (_activeTest == null)
            {
                LogRuntime(
                    "DEVELOPER_CHECKPOINT",
                    "TestId=none; Reason=Checkpoint requested without an active test.");
                Notify("Checkpoint recorded, but no Developer test is active.");
                return;
            }

            LSDeveloperCheckpoint checkpoint = new LSDeveloperCheckpoint
            {
                Name = "Checkpoint-" + (_activeTest.Checkpoints.Count + 1).ToString(CultureInfo.InvariantCulture),
                TimestampUtc = DateTime.UtcNow
            };
            _activeTest.Checkpoints.Add(checkpoint);
            LogRuntime(
                "DEVELOPER_TEST_CHECKPOINT",
                "TestId=" + _activeTest.TestId
                + "; Checkpoint=" + checkpoint.Name
                + "; RelevantActors=" + _activeTest.RelevantActors.Count.ToString(CultureInfo.InvariantCulture));
            FlushEvidence("test checkpoint " + checkpoint.Name);
            Notify(checkpoint.Name + " captured to LSDeveloperTrace.log.");
        }

        private void EndTestInternal(string reason, bool notify)
        {
            if (_activeTest == null)
            {
                if (notify)
                    Notify("No Developer test is active.");
                return;
            }

            LSDeveloperTestSession session = _activeTest;
            LogRuntime(
                "DEVELOPER_TEST_ENDED",
                "TestId=" + session.TestId
                + "; Reason=" + (reason ?? string.Empty));
            session.EndedAtUtc = DateTime.UtcNow;
            session.EndReason = reason ?? string.Empty;
            session.IsEnded = true;
            FlushEvidence("test end");
            _lastTest = session;
            _activeTest = null;
            _pendingFailureFlushUtc = DateTime.MinValue;

            if (notify)
                Notify("Developer test ended. Choose Analyze Current Test to classify the evidence.");
        }

        private void AnalyzeCurrentTest()
        {
            LSDeveloperTestSession session = _activeTest ?? _lastTest;
            if (session == null)
            {
                Notify("Start a Developer test before analyzing a behavior.");
                return;
            }

            if (_activeTest != null)
                FlushEvidence("explicit analysis");

            try
            {
                _lastAnalysis = AnalyzeSession(session);
                WriteAnalysisReport(_lastAnalysis);
                LogRuntime("DEVELOPER_TEST_ANALYZED", _lastAnalysis.Summary);
                LogDebug("DEVELOPER_FIRST_DIVERGENCE", _lastAnalysis.FirstDivergence);
                Notify(_lastAnalysis.ToNotification());
            }
            catch (Exception error)
            {
                LogException("DEVELOPER_TEST_ANALYSIS_FAILED", error);
                Notify("Developer test analysis failed; see LSDeveloperTrace.log and LSDebug.log.");
            }
        }

        private void CompareCheckpoints()
        {
            LSDeveloperTestSession session = _activeTest ?? _lastTest;
            if (session == null || session.Checkpoints.Count < 2)
            {
                Notify("At least two checkpoints in one Developer test are required.");
                return;
            }

            LSDeveloperCheckpoint first = session.Checkpoints[session.Checkpoints.Count - 2];
            LSDeveloperCheckpoint second = session.Checkpoints[session.Checkpoints.Count - 1];
            List<LSDeveloperTraceEvent> events = GetSessionEvents(session)
                .Where(item => item.TimestampUtc >= first.TimestampUtc && item.TimestampUtc <= second.TimestampUtc)
                .OrderBy(item => item.Sequence)
                .ToList();
            string summary = "Between " + first.Name + " and " + second.Name
                + ": Events=" + events.Count.ToString(CultureInfo.InvariantCulture)
                + "; Failures=" + events.Count(IsSerious).ToString(CultureInfo.InvariantCulture)
                + "; Actors=" + events.Select(item => item.ActorHandle).Where(item => item > 0).Distinct().Count().ToString(CultureInfo.InvariantCulture);
            LogRuntime("DEVELOPER_CHECKPOINT_COMPARISON", "TestId=" + session.TestId + "; " + summary);
            Notify(summary);
        }

        private void ShowLiveInspection()
        {
            if (_activeTest == null && _lastTest == null)
            {
                Notify("No Developer test actor has been observed.");
                return;
            }

            if (_activeTest != null)
                SampleActiveActors(DateTime.UtcNow);
            ShowCurrentActorEvidence();
        }

        private void ShowCurrentActorEvidence()
        {
            LSDeveloperTestSession session = _activeTest ?? _lastTest;
            if (session == null)
            {
                Notify("No Developer test actor has been observed.");
                return;
            }

            int handle = _selectedActorHandle;
            LSDeveloperTraceEvent latest = GetSessionEvents(session)
                .Where(item => (handle <= 0 || item.ActorHandle == handle)
                    && item.EventKind != "ObservedState")
                .OrderBy(item => item.Sequence)
                .LastOrDefault(item => item.ActorHandle > 0);
            if (latest == null)
            {
                Notify("No selected actor evidence is available yet.");
                return;
            }

            _selectedActorHandle = latest.ActorHandle;
            string live = ReadLiveActorSummary(latest.ActorHandle, latest);
            Notify(
                "Selected Actor=" + latest.ActorHandle
                + "; State=" + ValueOrUnknown(latest.StateAfter)
                + "; LastTask=" + ValueOrUnknown(latest.TaskAfter)
                + "; Owner=" + ValueOrUnknown(latest.Owner)
                + "; Target=" + HandleOrUnknown(latest.TargetHandle)
                + "; Vehicle=" + HandleOrUnknown(latest.VehicleHandle)
                + "; " + live);
        }

        private void ShowFirstDivergence()
        {
            if (_lastAnalysis == null)
            {
                Notify("Analyze a Developer test first.");
                return;
            }
            Notify("FIRST DIVERGENCE: " + _lastAnalysis.FirstDivergence);
        }

        private void ShowLatestFailure()
        {
            LSDeveloperTestSession session = _activeTest ?? _lastTest;
            if (session == null)
            {
                Notify("No Developer test evidence is available.");
                return;
            }
            LSDeveloperTraceEvent failure = GetSessionEvents(session)
                .Where(IsSerious)
                .OrderBy(item => item.Sequence)
                .LastOrDefault();
            Notify(failure == null
                ? "No serious failure event was observed in this test."
                : "Latest failure is observed evidence: " + DescribeEvent(failure));
        }

        private void ShowTimeline(string kind)
        {
            LSDeveloperTestSession session = _activeTest ?? _lastTest;
            if (session == null)
            {
                Notify("No Developer test evidence is available.");
                return;
            }

            IEnumerable<LSDeveloperTraceEvent> query = GetSessionEvents(session);
            if (kind == "StateTransition")
                query = query.Where(item => item.EventKind == "StateTransition" || !string.IsNullOrWhiteSpace(item.StateAfter));
            else if (kind == "Task")
                query = query.Where(item => item.EventKind == "TaskIssued" || item.EventKind == "TaskConflict");
            else if (kind == "Behavioral")
                query = query.Where(item => item.EventKind == "ObservedState" || item.EventKind == "BehaviorStall");
            else if (kind == "Ownership")
                query = query.Where(item => item.EventKind == "Ownership" || item.EventKind == "TaskConflict");
            else if (kind == "TaskConflict")
                query = query.Where(item => item.EventKind == "TaskConflict");
            else if (kind == "PhysicalMismatch")
                query = query.Where(item => item.EventKind == "PhysicalMismatch");
            else if (kind == "BehaviorStall")
                query = query.Where(item => item.EventKind == "BehaviorStall");
            else if (kind == "Systems")
                query = query.Where(item => item.Owner == "Dispatch"
                    || item.Owner == "Backup"
                    || item.Owner == "Convoy"
                    || item.Owner == "CrimeActivity");

            List<LSDeveloperTraceEvent> selected = LastItems(query.OrderBy(item => item.Sequence), 8);
            if (selected.Count == 0)
            {
                Notify("No " + kind + " evidence is available in this test.");
                return;
            }
            Notify(kind + ": " + string.Join(" || ", selected.Select(DescribeEvent).ToArray()));
        }

        private string ReadLiveActorSummary(int actorHandle, LSDeveloperTraceEvent latest)
        {
            try
            {
                bool exists = Function.Call<bool>(Hash.DOES_ENTITY_EXIST, actorHandle);
                if (!exists)
                    return "Physical=CONFIRMED_ACTOR_HANDLE_NO_LONGER_EXISTS";

                Vector3 position = Function.Call<Vector3>(Hash.GET_ENTITY_COORDS, actorHandle, true);
                float speed = Function.Call<float>(Hash.GET_ENTITY_SPEED, actorHandle);
                int model = Function.Call<int>(Hash.GET_ENTITY_MODEL, actorHandle);
                int vehicle = 0;
                bool isPed = Function.Call<bool>(Hash.IS_ENTITY_A_PED, actorHandle);
                if (isPed)
                    vehicle = Function.Call<int>(Hash.GET_VEHICLE_PED_IS_IN, actorHandle, false);

                LSDeveloperPhysicalObservation physical = ReadPhysicalObservation(
                    actorHandle,
                    isPed,
                    latest,
                    latest == null ? 0 : latest.TargetHandle,
                    speed,
                    float.NaN);

                float distance = float.NaN;
                if (latest.TargetHandle > 0
                    && Function.Call<bool>(Hash.DOES_ENTITY_EXIST, latest.TargetHandle))
                {
                    Vector3 targetPosition = Function.Call<Vector3>(
                        Hash.GET_ENTITY_COORDS,
                        latest.TargetHandle,
                        true);
                    distance = position.DistanceTo(targetPosition);
                }

                return "Physical=OBSERVED; Exists=true; Speed=" + speed.ToString("0.0", CultureInfo.InvariantCulture)
                    + "; Position=" + position
                    + "; ModelHash=" + model.ToString(CultureInfo.InvariantCulture)
                    + "; CurrentVehicle=" + HandleOrUnknown(vehicle)
                    + "; Behavior=" + ValueOrUnknown(physical.PhysicalBehavior)
                    + "; TaskStatus=" + ValueOrUnknown(physical.TaskStatus)
                    + "; WeaponHash=" + physical.CurrentWeaponHash.ToString(CultureInfo.InvariantCulture)
                    + "; Shooting=" + physical.IsShooting
                    + "; InCombat=" + physical.IsInCombat
                    + "; AimingFromCover=" + physical.IsAimingFromCover
                    + "; ArrestTaskNative=" + physical.IsRunningArrestTask
                    + "; CombatTarget=" + HandleOrUnknown(physical.CombatTargetHandle)
                    + "; TargetDistance=" + (float.IsNaN(distance)
                        ? "unknown" : distance.ToString("0.0", CultureInfo.InvariantCulture));
            }
            catch (Exception error)
            {
                LogDebug("DEVELOPER_LIVE_INSPECTION_FAILED", error.Message);
                return "Physical=UNKNOWN; Live native inspection failed";
            }
        }

        private LSDeveloperPhysicalObservation ReadPhysicalObservation(
            int actorHandle,
            bool isPed,
            LSDeveloperTraceEvent latest,
            int targetHandle,
            float speed,
            float moved)
        {
            LSDeveloperPhysicalObservation result = new LSDeveloperPhysicalObservation
            {
                PhysicalBehavior = "UNKNOWN",
                TaskStatus = "UNKNOWN"
            };

            if (!_settings.LiveBehavioralConfirmation)
            {
                result.PhysicalBehavior = "DISABLED_BY_DEVELOPER_CONFIGURATION";
                result.TaskStatus = "DISABLED_BY_DEVELOPER_CONFIGURATION";
                return result;
            }

            if (!isPed)
            {
                result.PhysicalBehavior = speed < 0.25f
                    ? "VEHICLE_STATIONARY"
                    : "VEHICLE_MOVING_OR_UNKNOWN";
                result.TaskStatus = "NOT_APPLICABLE_TO_NON_PED_ACTOR";
                return result;
            }

            try
            {
                result.CurrentWeaponHash = Function.Call<int>(
                    Hash.GET_SELECTED_PED_WEAPON,
                    actorHandle);
            }
            catch
            {
                result.CurrentWeaponHash = 0;
            }

            try
            {
                result.IsShooting = Function.Call<bool>(Hash.IS_PED_SHOOTING, actorHandle);
            }
            catch
            {
                result.IsShooting = false;
            }

            try
            {
                result.IsInCombat = Function.Call<bool>(
                    Hash.IS_PED_IN_COMBAT,
                    actorHandle,
                    targetHandle > 0 ? targetHandle : 0);
            }
            catch
            {
                result.IsInCombat = false;
            }

            try
            {
                result.IsAimingFromCover = Function.Call<bool>(
                    Hash.IS_PED_AIMING_FROM_COVER,
                    actorHandle);
            }
            catch
            {
                result.IsAimingFromCover = false;
            }

            try
            {
                result.IsRunningArrestTask = Function.Call<bool>(
                    Hash.IS_PED_RUNNING_ARREST_TASK,
                    actorHandle);
            }
            catch
            {
                result.IsRunningArrestTask = false;
            }

            try
            {
                result.CombatTargetHandle = Function.Call<int>(
                    Hash.GET_PED_TARGET_FROM_COMBAT_PED,
                    actorHandle,
                    true);
            }
            catch
            {
                result.CombatTargetHandle = 0;
            }

            result.TaskStatus = ReadScriptTaskStatus(
                actorHandle,
                latest == null ? string.Empty : latest.TaskAfter);

            List<string> behavior = new List<string>();
            if (result.IsShooting)
                behavior.Add("SHOOTING");
            if (result.IsAimingFromCover)
                behavior.Add("AIMING_FROM_COVER");
            if (result.IsInCombat)
                behavior.Add("IN_COMBAT");
            if (result.IsRunningArrestTask)
                behavior.Add("ARREST_TASK_NATIVE");
            if (result.CurrentWeaponHash != 0)
                behavior.Add("WEAPON_EQUIPPED");

            bool stationary = speed < 0.25f
                && !float.IsNaN(moved)
                && moved < 0.75f;
            behavior.Add(stationary ? "STATIONARY" : "MOVING_OR_UNKNOWN");
            result.PhysicalBehavior = string.Join("+", behavior.ToArray());
            return result;
        }

        private static string ReadScriptTaskStatus(int actorHandle, string task)
        {
            Hash taskHash;
            string taskName;
            if (!TryResolveScriptTaskHash(task, out taskHash, out taskName))
                return "UNRESOLVED_FOR_LAST_TASK";

            try
            {
                int status = Function.Call<int>(
                    Hash.GET_SCRIPT_TASK_STATUS,
                    actorHandle,
                    taskHash);
                // Keep the native status raw.  GTA task status values are
                // useful evidence, but Developer Mode must not guess that a
                // numeric result means "completed" or "failed" across builds.
                return taskName + "_STATUS=" + status.ToString(CultureInfo.InvariantCulture);
            }
            catch
            {
                return taskName + "_STATUS=UNKNOWN";
            }
        }

        private static bool TryResolveScriptTaskHash(
            string task,
            out Hash taskHash,
            out string taskName)
        {
            string value = (task ?? string.Empty).ToUpperInvariant();
            taskHash = Hash.TASK_ARREST_PED;
            taskName = string.Empty;
            if (value.Contains("TASK_ARREST_PED"))
            {
                taskHash = Hash.TASK_ARREST_PED;
                taskName = "TASK_ARREST_PED";
                return true;
            }
            if (value.Contains("TASK_ENTER_VEHICLE"))
            {
                taskHash = Hash.TASK_ENTER_VEHICLE;
                taskName = "TASK_ENTER_VEHICLE";
                return true;
            }
            if (value.Contains("TASK_FOLLOW_NAV_MESH_TO_COORD"))
            {
                taskHash = Hash.TASK_FOLLOW_NAV_MESH_TO_COORD;
                taskName = "TASK_FOLLOW_NAV_MESH_TO_COORD";
                return true;
            }
            if (value.Contains("TASK_AIM_GUN_AT_ENTITY"))
            {
                taskHash = Hash.TASK_AIM_GUN_AT_ENTITY;
                taskName = "TASK_AIM_GUN_AT_ENTITY";
                return true;
            }
            if (value.Contains("TASK_TURN_PED_TO_FACE_ENTITY"))
            {
                taskHash = Hash.TASK_TURN_PED_TO_FACE_ENTITY;
                taskName = "TASK_TURN_PED_TO_FACE_ENTITY";
                return true;
            }
            if (value.Contains("TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE"))
            {
                taskHash = Hash.TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE;
                taskName = "TASK_VEHICLE_DRIVE_TO_COORD_LONGRANGE";
                return true;
            }
            if (value.Contains("TASK_FOLLOW_TO_OFFSET_OF_ENTITY"))
            {
                taskHash = Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY;
                taskName = "TASK_FOLLOW_TO_OFFSET_OF_ENTITY";
                return true;
            }
            if (value.Contains("TASK_GO_TO_ENTITY"))
            {
                taskHash = Hash.TASK_GO_TO_ENTITY;
                taskName = "TASK_GO_TO_ENTITY";
                return true;
            }
            if (value.Contains("TASK_GO_STRAIGHT_TO_COORD"))
            {
                taskHash = Hash.TASK_GO_STRAIGHT_TO_COORD;
                taskName = "TASK_GO_STRAIGHT_TO_COORD";
                return true;
            }
            if (value.Contains("TASK_PLAY_ANIM"))
            {
                taskHash = Hash.TASK_PLAY_ANIM;
                taskName = "TASK_PLAY_ANIM";
                return true;
            }
            if (value.Contains("TASK_LEAVE_VEHICLE"))
            {
                taskHash = Hash.TASK_LEAVE_VEHICLE;
                taskName = "TASK_LEAVE_VEHICLE";
                return true;
            }
            if (value.Contains("TASK_COMBAT"))
            {
                taskHash = Hash.TASK_COMBAT_PED;
                taskName = "TASK_COMBAT_PED";
                return true;
            }
            if (value.Contains("TASK_SMART_FLEE_PED"))
            {
                taskHash = Hash.TASK_SMART_FLEE_PED;
                taskName = "TASK_SMART_FLEE_PED";
                return true;
            }
            if (value.Contains("TASK_REACT_AND_FLEE_PED"))
            {
                taskHash = Hash.TASK_REACT_AND_FLEE_PED;
                taskName = "TASK_REACT_AND_FLEE_PED";
                return true;
            }
            return false;
        }

        private void SampleActiveActors(DateTime now)
        {
            LSDeveloperTestSession session = _activeTest;
            if (session == null)
                return;

            int[] handles;
            lock (_sync)
            {
                handles = session.RelevantActors
                    .OrderByDescending(item => item == _selectedActorHandle)
                    .Take(8)
                    .ToArray();
            }

            foreach (int actorHandle in handles)
            {
                try
                {
                    bool exists = Function.Call<bool>(Hash.DOES_ENTITY_EXIST, actorHandle);
                    if (!exists)
                    {
                        AddManualTrace(new LSDeveloperTraceEvent
                        {
                            TimestampUtc = now,
                            RuntimeSessionId = _log == null ? string.Empty : _log.SessionId,
                            Owner = "Developer",
                            Category = "LIVE_ACTOR_LOST",
                            EventKind = "TargetFailure",
                            ActorHandle = actorHandle,
                            StateAfter = LatestState(actorHandle),
                            Context = "Selective sample: actor handle no longer exists."
                        });
                        continue;
                    }

                    Vector3 position = Function.Call<Vector3>(Hash.GET_ENTITY_COORDS, actorHandle, true);
                    float speed = Function.Call<float>(Hash.GET_ENTITY_SPEED, actorHandle);
                    int model = Function.Call<int>(Hash.GET_ENTITY_MODEL, actorHandle);
                    int vehicle = 0;
                    bool isPed = Function.Call<bool>(Hash.IS_ENTITY_A_PED, actorHandle);
                    if (isPed)
                        vehicle = Function.Call<int>(Hash.GET_VEHICLE_PED_IS_IN, actorHandle, false);

                    LSDeveloperTraceEvent previousSample;
                    session.LastSamples.TryGetValue(actorHandle, out previousSample);
                    LSDeveloperTraceEvent latest = LatestActorEvent(actorHandle);
                    string state = LatestState(actorHandle);
                    if (string.IsNullOrWhiteSpace(state)
                        && latest != null
                        && latest.TargetHandle > 0)
                    {
                        // A task is often issued to the Backup officer while the
                        // logical interaction state belongs to the citizen. Keep
                        // the observation selective, but carry that related state
                        // to the task actor so a stalled officer is diagnosable.
                        state = LatestState(latest.TargetHandle);
                    }
                    int targetHandle = latest == null ? 0 : latest.TargetHandle;
                    if (targetHandle == actorHandle)
                        targetHandle = 0;
                    string targetModel = string.Empty;
                    float targetDistance = float.NaN;
                    if (targetHandle > 0
                        && Function.Call<bool>(Hash.DOES_ENTITY_EXIST, targetHandle))
                    {
                        Vector3 targetPosition = Function.Call<Vector3>(
                            Hash.GET_ENTITY_COORDS,
                            targetHandle,
                            true);
                        targetDistance = position.DistanceTo(targetPosition);
                        targetModel = Function.Call<int>(Hash.GET_ENTITY_MODEL, targetHandle)
                            .ToString(CultureInfo.InvariantCulture);
                    }
                    string vehicleModel = vehicle > 0
                        && Function.Call<bool>(Hash.DOES_ENTITY_EXIST, vehicle)
                        ? Function.Call<int>(Hash.GET_ENTITY_MODEL, vehicle)
                            .ToString(CultureInfo.InvariantCulture)
                        : string.Empty;
                    float moved = previousSample == null
                        ? float.NaN
                        : position.DistanceTo(previousSample.Position);
                    LSDeveloperPhysicalObservation physical = ReadPhysicalObservation(
                        actorHandle,
                        isPed,
                        latest,
                        targetHandle,
                        speed,
                        moved);
                    if (targetHandle <= 0 && physical.CombatTargetHandle > 0)
                    {
                        targetHandle = physical.CombatTargetHandle;
                        if (Function.Call<bool>(Hash.DOES_ENTITY_EXIST, targetHandle))
                        {
                            Vector3 targetPosition = Function.Call<Vector3>(
                                Hash.GET_ENTITY_COORDS,
                                targetHandle,
                                true);
                            targetDistance = position.DistanceTo(targetPosition);
                            targetModel = Function.Call<int>(Hash.GET_ENTITY_MODEL, targetHandle)
                                .ToString(CultureInfo.InvariantCulture);
                        }
                    }

                    LSDeveloperTraceEvent sample = new LSDeveloperTraceEvent
                    {
                        TimestampUtc = now,
                        RuntimeSessionId = _log == null ? string.Empty : _log.SessionId,
                        Owner = latest == null ? "Unknown" : latest.Owner,
                        Category = "LIVE_ACTOR_SAMPLE",
                        EventKind = "ObservedState",
                        ActorHandle = actorHandle,
                        ActorModel = model.ToString(CultureInfo.InvariantCulture),
                        TargetHandle = targetHandle,
                        TargetModel = targetModel,
                        VehicleHandle = vehicle,
                        VehicleModel = vehicleModel,
                        StateAfter = state,
                        TaskAfter = latest == null ? string.Empty : latest.TaskAfter,
                        ExpectedNext = ExpectedContinuationFor(session, actorHandle, state),
                        SourceFile = latest == null ? string.Empty : latest.SourceFile,
                        SourceMember = latest == null ? string.Empty : latest.SourceMember,
                        PhysicalBehavior = physical.PhysicalBehavior,
                        TaskStatus = physical.TaskStatus,
                        CurrentWeaponHash = physical.CurrentWeaponHash,
                        IsShooting = physical.IsShooting,
                        IsInCombat = physical.IsInCombat,
                        IsAimingFromCover = physical.IsAimingFromCover,
                        IsRunningArrestTask = physical.IsRunningArrestTask,
                        CombatTargetHandle = physical.CombatTargetHandle,
                        HasPosition = true,
                        Position = position,
                        Distance = float.IsNaN(targetDistance) ? moved : targetDistance,
                        TargetDistance = targetDistance,
                        MovementDelta = moved,
                        Context = "ObservedState=" + ValueOrUnknown(state)
                            + "; LastIssuedTask=" + ValueOrUnknown(latest == null ? string.Empty : latest.TaskAfter)
                            + "; TaskStatus=" + ValueOrUnknown(physical.TaskStatus)
                            + "; PhysicalBehavior=" + ValueOrUnknown(physical.PhysicalBehavior)
                            + "; WeaponHash=" + physical.CurrentWeaponHash.ToString(CultureInfo.InvariantCulture)
                            + "; Shooting=" + physical.IsShooting
                            + "; InCombat=" + physical.IsInCombat
                            + "; AimingFromCover=" + physical.IsAimingFromCover
                            + "; CombatTarget=" + HandleOrUnknown(physical.CombatTargetHandle)
                            + "; MovementDelta=" + (float.IsNaN(moved)
                                ? "unknown" : moved.ToString("0.0", CultureInfo.InvariantCulture))
                            + "; TargetDistance=" + (float.IsNaN(targetDistance)
                                ? "unknown" : targetDistance.ToString("0.0", CultureInfo.InvariantCulture))
                            + "; Speed=" + speed.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; ModelHash=" + model.ToString(CultureInfo.InvariantCulture)
                            + "; CurrentVehicle=" + vehicle.ToString(CultureInfo.InvariantCulture)
                    };
                    AddManualTrace(sample);

                    session.LastSamples[actorHandle] = new LSDeveloperTraceEvent
                    {
                        TimestampUtc = now,
                        ActorHandle = actorHandle,
                        HasPosition = true,
                        Position = position,
                        Distance = speed
                    };
                    DetectPhysicalMismatch(
                        session,
                        actorHandle,
                        state,
                        speed,
                        vehicle,
                        moved,
                        now,
                        sample,
                        physical);
                    DetectBehaviorStall(
                        session,
                        actorHandle,
                        state,
                        latest,
                        sample,
                        physical,
                        now);
                }
                catch (Exception error)
                {
                    AddManualTrace(new LSDeveloperTraceEvent
                    {
                        TimestampUtc = now,
                        RuntimeSessionId = _log == null ? string.Empty : _log.SessionId,
                        Owner = "Developer",
                        Category = "LIVE_ACTOR_SAMPLE_FAILED",
                        EventKind = "Failure",
                        ActorHandle = actorHandle,
                        Reason = error.Message,
                        Context = "Selective actor sampling did not complete."
                    });
                }
            }
        }

        private void DetectPhysicalMismatch(
            LSDeveloperTestSession session,
            int actorHandle,
            string state,
            float speed,
            int vehicleHandle,
            float moved,
            DateTime now,
            LSDeveloperTraceEvent sample,
            LSDeveloperPhysicalObservation physical)
        {
            if (string.IsNullOrWhiteSpace(state))
                return;

            string upper = state.ToUpperInvariant();
            // BackupSecuring and BackupEscort intentionally contain a short
            // stationary arrest/animation handoff. Let the timed behavioral
            // confirmation decide whether that handoff became a stall instead
            // of reporting a false mismatch on its second sample.
            if (upper.Contains("BACKUPSECURING") || upper.Contains("BACKUPESCORT"))
                return;
            bool expectedMovement = upper.Contains("FLEE")
                || upper.Contains("ESCORT")
                || upper.Contains("TRANSPORT")
                || upper.Contains("FOLLOW")
                || upper.Contains("ENROUTE")
                || upper.Contains("EN_ROUTE");
            bool stationary = !float.IsNaN(moved)
                && speed < 0.25f
                && moved < 0.75f;
            bool transportWithoutVehicle = upper.Contains("TRANSPORT") && vehicleHandle <= 0;
            if ((!expectedMovement || !stationary) && !transportWithoutVehicle)
                return;

            string key = actorHandle.ToString(CultureInfo.InvariantCulture) + "|" + state;
            if (!session.ReportedPhysicalMismatches.Add(key))
                return;

            AddManualTrace(new LSDeveloperTraceEvent
            {
                TimestampUtc = now,
                RuntimeSessionId = _log == null ? string.Empty : _log.SessionId,
                Owner = "Developer",
                Category = "STATE_PHYSICAL_MISMATCH",
                EventKind = "PhysicalMismatch",
                ActorHandle = actorHandle,
                TargetHandle = sample == null ? 0 : sample.TargetHandle,
                VehicleHandle = vehicleHandle,
                StateAfter = state,
                ExpectedNext = sample == null ? string.Empty : sample.ExpectedNext,
                SourceFile = sample == null ? string.Empty : sample.SourceFile,
                SourceMember = sample == null ? string.Empty : sample.SourceMember,
                PhysicalBehavior = physical == null ? string.Empty : physical.PhysicalBehavior,
                TaskStatus = physical == null ? string.Empty : physical.TaskStatus,
                Reason = transportWithoutVehicle
                    ? "Logical transport state observed while no current vehicle was observed."
                    : "Logical movement state observed while speed and position were stationary.",
                Distance = moved,
                MovementDelta = moved,
                Context = "Expected physical movement was not confirmed; Developer Mode did not change the actor task."
            });
        }

        private void DetectBehaviorStall(
            LSDeveloperTestSession session,
            int actorHandle,
            string state,
            LSDeveloperTraceEvent latest,
            LSDeveloperTraceEvent sample,
            LSDeveloperPhysicalObservation physical,
            DateTime now)
        {
            if (session == null
                || !_settings.LiveBehavioralConfirmation
                || physical == null
                || !IsPotentialBehaviorStall(state, latest, physical))
                return;

            string expected = sample == null
                ? ExpectedContinuationFor(session, actorHandle, state)
                : sample.ExpectedNext;
            if (string.IsNullOrWhiteSpace(expected))
                return;

            string key = actorHandle.ToString(CultureInfo.InvariantCulture)
                + "|" + state + "|" + expected;
            LSDeveloperBehaviorObservation since;
            if (!session.BehaviorSince.TryGetValue(actorHandle, out since)
                || !string.Equals(since.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                session.BehaviorSince[actorHandle] = new LSDeveloperBehaviorObservation
                {
                    Key = key,
                    SinceUtc = now
                };
                return;
            }

            TimeSpan duration = now - since.SinceUtc;
            if (duration < TimeSpan.FromSeconds(_settings.BehavioralStallSeconds)
                || !session.ReportedBehaviorStalls.Add(key))
                return;

            LSDeveloperTraceEvent stall = new LSDeveloperTraceEvent
            {
                TimestampUtc = now,
                RuntimeSessionId = _log == null ? string.Empty : _log.SessionId,
                Owner = latest == null ? "Unknown" : latest.Owner,
                Category = "DEVELOPER_BEHAVIOR_STALL",
                EventKind = "BehaviorStall",
                ActorHandle = actorHandle,
                TargetHandle = sample == null ? 0 : sample.TargetHandle,
                VehicleHandle = sample == null ? 0 : sample.VehicleHandle,
                StateAfter = state,
                TaskAfter = latest == null ? string.Empty : latest.TaskAfter,
                ExpectedNext = expected,
                SourceFile = sample == null ? string.Empty : sample.SourceFile,
                SourceMember = sample == null ? string.Empty : sample.SourceMember,
                PhysicalBehavior = physical.PhysicalBehavior,
                TaskStatus = physical.TaskStatus,
                CurrentWeaponHash = physical.CurrentWeaponHash,
                IsShooting = physical.IsShooting,
                IsInCombat = physical.IsInCombat,
                IsAimingFromCover = physical.IsAimingFromCover,
                IsRunningArrestTask = physical.IsRunningArrestTask,
                CombatTargetHandle = physical.CombatTargetHandle,
                Distance = sample == null ? float.NaN : sample.Distance,
                TargetDistance = sample == null ? float.NaN : sample.TargetDistance,
                MovementDelta = sample == null ? float.NaN : sample.MovementDelta,
                HasPosition = sample != null && sample.HasPosition,
                Position = sample == null ? Vector3.Zero : sample.Position,
                Reason = "LogicalState=" + ValueOrUnknown(state)
                    + "; PhysicalBehavior=" + ValueOrUnknown(physical.PhysicalBehavior)
                    + "; LastIssuedTask=" + ValueOrUnknown(latest == null ? string.Empty : latest.TaskAfter)
                    + "; TaskStatus=" + ValueOrUnknown(physical.TaskStatus)
                    + "; ExpectedNext=" + expected
                    + "; No expected transition was observed for "
                    + duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " seconds.",
                Context = "CONFIRMED: selected actor remained in the sampled logical/physical condition. "
                    + "INFERENCE: the documented continuation did not occur. "
                    + "UNKNOWN: the lower-level GTA reason unless task status, rejection, timeout, or exception evidence exposes it. "
                    + "Developer Mode did not replace or recover the gameplay task."
            };
            AddManualTrace(stall);
            Notify("Developer captured a behavioral stall: " + Limit(expected, 180) + ".");
        }

        private static bool IsPotentialBehaviorStall(
            string state,
            LSDeveloperTraceEvent latest,
            LSDeveloperPhysicalObservation physical)
        {
            if (string.IsNullOrWhiteSpace(state)
                || latest == null
                || string.IsNullOrWhiteSpace(latest.TaskAfter)
                || physical == null)
                return false;

            string upper = state.ToUpperInvariant();
            bool monitoredStage = upper.Contains("BACKUPSECURING")
                || upper.Contains("BACKUPESCORT")
                || upper.Contains("BACKUPTRANSPORT")
                || upper.Contains("AWAITINGBACKUP")
                || upper.Contains("FLEEING")
                || upper.Contains("ESCORT")
                || upper.Contains("TRANSPORT");
            if (!monitoredStage)
                return false;

            bool stationary = (physical.PhysicalBehavior ?? string.Empty)
                .IndexOf("STATIONARY", StringComparison.OrdinalIgnoreCase) >= 0;
            bool weaponHold = physical.CurrentWeaponHash != 0
                && (physical.IsInCombat || physical.IsAimingFromCover || stationary);
            if (upper.Contains("BACKUPSECURING"))
                return stationary || weaponHold;
            if (upper.Contains("BACKUPESCORT"))
                return stationary || weaponHold;
            if (upper.Contains("BACKUPTRANSPORT"))
                return stationary;
            return stationary;
        }

        private static string ExpectedContinuationFor(
            LSDeveloperTestSession session,
            int actorHandle,
            string state)
        {
            if (string.IsNullOrWhiteSpace(state))
                return string.Empty;

            string upper = state.ToUpperInvariant()
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty);
            List<LSDeveloperTraceEvent> events = session == null
                ? new List<LSDeveloperTraceEvent>()
                : session.RecentEvents.ToList();

            if (upper.Contains("BACKUPSECURING"))
                return "NPC_BACKUP_SUBJECT_SECURED -> state BackupEscort";

            if (upper.Contains("BACKUPESCORT"))
            {
                bool entryIssued = events.Any(item =>
                    item.Category.IndexOf("PRISONER_ENTRY_TASK_ISSUED", StringComparison.OrdinalIgnoreCase) >= 0);
                if (entryIssued)
                    return "NPC_BACKUP_SUBJECT_ENTERED_VEHICLE -> state BackupTransport";

                bool doorOpened = events.Any(item =>
                    item.Category.IndexOf("PRISONER_DOOR_OPENED", StringComparison.OrdinalIgnoreCase) >= 0
                    || item.Category.IndexOf("ESCORT_REACHED_VEHICLE", StringComparison.OrdinalIgnoreCase) >= 0);
                if (doorOpened)
                    return "NPC_BACKUP_PRISONER_ENTRY_TASK_ISSUED -> subject enters rear seat";

                bool movementStarted = events.Any(item =>
                    item.Category.IndexOf("ESCORT_MOVEMENT_STARTED", StringComparison.OrdinalIgnoreCase) >= 0
                    || item.Category.IndexOf("ESCORT_TO_VEHICLE_STARTED", StringComparison.OrdinalIgnoreCase) >= 0);
                return movementStarted
                    ? "NPC_BACKUP_ESCORT_REACHED_VEHICLE -> NPC_BACKUP_PRISONER_DOOR_OPENED"
                    : "NPC_BACKUP_ESCORT_MOVEMENT_STARTED -> NPC_BACKUP_ESCORT_TO_VEHICLE_STARTED";
            }

            if (upper.Contains("BACKUPTRANSPORT"))
                return "transport departure -> station arrival -> custody handoff";
            if (upper.Contains("AWAITINGBACKUP"))
                return "Backup arrival -> intervention/containment";
            if (upper.Contains("FLEEING"))
                return "confirmed physical movement -> interception or controlled handoff";
            if (upper.Contains("ESCORT"))
                return "escort movement -> vehicle interaction -> entry";
            if (upper.Contains("TRANSPORT"))
                return "transport movement -> station arrival";
            return "next documented state/task transition for the selected branch";
        }

        private string LatestState(int actorHandle)
        {
            LSDeveloperTestSession session = _activeTest;
            if (session == null)
                return string.Empty;

            // Raw lifecycle messages can be written immediately after the
            // central state setter and may contain a broad inferred label such
            // as ARRESTED_OR_SECURING. Prefer the instrumented logical state
            // transition so the observer does not replace BackupEscort with a
            // less precise log-derived description.
            LSDeveloperTraceEvent latest = session.RecentEvents
                .Where(item => item.ActorHandle == actorHandle
                    && item.EventKind == "StateTransition"
                    && !string.IsNullOrWhiteSpace(item.StateAfter))
                .OrderBy(item => item.Sequence)
                .LastOrDefault();
            if (latest != null)
                return latest.StateAfter;

            latest = LatestActorEvent(actorHandle);
            return latest == null ? string.Empty : latest.StateAfter;
        }

        private LSDeveloperTraceEvent LatestActorEvent(int actorHandle)
        {
            LSDeveloperTestSession session = _activeTest;
            if (session == null)
                return null;
            return session.RecentEvents
                .Where(item => item.ActorHandle == actorHandle
                    && item.EventKind != "ObservedState")
                .OrderBy(item => item.Sequence)
                .LastOrDefault();
        }

        private void AddManualTrace(LSDeveloperTraceEvent trace)
        {
            if (trace == null)
                return;
            lock (_sync)
                AddTraceLocked(trace);
        }

        private void AddTraceLocked(LSDeveloperTraceEvent trace)
        {
            if (trace == null || _shutDown || !_settings.Enabled)
                return;

            trace.Sequence = ++_nextSequence;
            if (string.IsNullOrWhiteSpace(trace.RuntimeSessionId) && _log != null)
                trace.RuntimeSessionId = _log.SessionId;
            if (_activeTest != null && string.IsNullOrWhiteSpace(trace.TestSessionId))
                trace.TestSessionId = _activeTest.TestId;
            trace.Owner = string.IsNullOrWhiteSpace(trace.Owner)
                ? "Unknown" : trace.Owner;
            trace.Category = string.IsNullOrWhiteSpace(trace.Category)
                ? "UNCLASSIFIED_EVENT" : trace.Category;
            if (string.IsNullOrWhiteSpace(trace.SourceFile))
                trace.SourceFile = LSDeveloperTraceEvent.InferSourceFile(trace.Category, trace.Owner);
            if (string.IsNullOrWhiteSpace(trace.SourceMember))
                trace.SourceMember = LSDeveloperTraceEvent.InferSourceMember(trace.Category, trace.Owner);
            trace.Context = Limit(trace.Context, 1600);

            _traceBuffer.Enqueue(trace);
            PruneTraceLocked(trace.TimestampUtc);

            if (_activeTest != null && !string.IsNullOrWhiteSpace(trace.TestSessionId)
                && trace.TestSessionId == _activeTest.TestId)
            {
                _activeTest.RecentEvents.Enqueue(trace);
                while (_activeTest.RecentEvents.Count > _settings.MaximumTraceEvents)
                    _activeTest.RecentEvents.Dequeue();
                if (trace.ActorHandle > 0)
                {
                    _activeTest.RelevantActors.Add(trace.ActorHandle);
                    _selectedActorHandle = trace.ActorHandle;
                }
                if (trace.TargetHandle > 0)
                    _activeTest.RelevantActors.Add(trace.TargetHandle);
                if (trace.VehicleHandle > 0)
                    _activeTest.RelevantActors.Add(trace.VehicleHandle);
            }

            TrackStateLocked(trace);
            TrackTaskConflictLocked(trace);
            if (_settings.AutomaticAnomalyDetection && IsSerious(trace))
                ScheduleFailureFlushLocked(trace);
        }

        private void TrackStateLocked(LSDeveloperTraceEvent trace)
        {
            if (trace.ActorHandle <= 0 || string.IsNullOrWhiteSpace(trace.StateAfter))
                return;

            string key = trace.Owner + "|" + trace.ActorHandle.ToString(CultureInfo.InvariantCulture);
            string previous;
            if (string.IsNullOrWhiteSpace(trace.StateBefore)
                && _lastStates.TryGetValue(key, out previous))
                trace.StateBefore = previous;
            _lastStates[key] = trace.StateAfter;
            if (_lastStates.Count > 1024)
                _lastStates.Clear();
        }

        private void TrackTaskConflictLocked(LSDeveloperTraceEvent trace)
        {
            if (trace.ActorHandle <= 0 || string.IsNullOrWhiteSpace(trace.TaskAfter)
                || trace.EventKind != "TaskIssued")
                return;

            LSDeveloperTaskObservation previous;
            if (_lastTasks.TryGetValue(trace.ActorHandle, out previous)
                && trace.TimestampUtc - previous.TimestampUtc <= TimeSpan.FromSeconds(4)
                && !string.Equals(previous.Owner, trace.Owner, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(previous.Task, trace.TaskAfter, StringComparison.OrdinalIgnoreCase))
            {
                LSDeveloperTraceEvent conflict = new LSDeveloperTraceEvent
                {
                    TimestampUtc = trace.TimestampUtc,
                    RuntimeSessionId = trace.RuntimeSessionId,
                    TestSessionId = trace.TestSessionId,
                    Owner = "Developer",
                    Category = "TASK_OWNERSHIP_CONFLICT",
                    EventKind = "TaskConflict",
                    ActorHandle = trace.ActorHandle,
                    TargetHandle = trace.TargetHandle,
                    VehicleHandle = trace.VehicleHandle,
                    TaskBefore = previous.Task,
                    TaskAfter = trace.TaskAfter,
                    ExpectedNext = trace.ExpectedNext,
                    SourceFile = trace.SourceFile,
                    SourceMember = trace.SourceMember,
                    Reason = "FirstOwner=" + previous.Owner
                        + "; SecondOwner=" + trace.Owner
                        + "; FirstTaskStillActive=UNKNOWN",
                    Context = "Conflict reported only. Developer Mode did not choose a task owner."
                };
                conflict.Sequence = ++_nextSequence;
                _traceBuffer.Enqueue(conflict);
                PruneTraceLocked(conflict.TimestampUtc);
                if (_activeTest != null && conflict.TestSessionId == _activeTest.TestId)
                {
                    _activeTest.RecentEvents.Enqueue(conflict);
                    while (_activeTest.RecentEvents.Count > _settings.MaximumTraceEvents)
                        _activeTest.RecentEvents.Dequeue();
                }
                ScheduleFailureFlushLocked(conflict);
            }

            _lastTasks[trace.ActorHandle] = new LSDeveloperTaskObservation
            {
                TimestampUtc = trace.TimestampUtc,
                Owner = trace.Owner,
                Task = trace.TaskAfter
            };
        }

        private void ScheduleFailureFlushLocked(LSDeveloperTraceEvent trace)
        {
            if (_activeTest == null)
                return;
            if (_activeTest.FirstAnomalySequence == 0)
            {
                _activeTest.FirstAnomalySequence = trace.Sequence;
                _activeTest.FirstAnomalyUtc = trace.TimestampUtc;
            }
            DateTime due = trace.TimestampUtc.AddSeconds(_settings.PostFailureCaptureSeconds);
            if (_pendingFailureFlushUtc == DateTime.MinValue || due < _pendingFailureFlushUtc)
                _pendingFailureFlushUtc = due;
        }

        private void PruneTraceLocked(DateTime now)
        {
            DateTime cutoff = now.AddSeconds(-_settings.TraceBufferSeconds);
            while (_traceBuffer.Count > 0)
            {
                LSDeveloperTraceEvent first = _traceBuffer.Peek();
                if (_traceBuffer.Count <= _settings.MaximumTraceEvents
                    && first.TimestampUtc >= cutoff)
                    break;
                _traceBuffer.Dequeue();
            }
        }

        private void FlushEvidence(string reason)
        {
            if (!_settings.WriteTraceFile || string.IsNullOrWhiteSpace(_paths.DeveloperTraceLogPath))
                return;

            List<LSDeveloperTraceEvent> events;
            LSDeveloperTestSession session = _activeTest ?? _lastTest;
            lock (_sync)
            {
                DateTime now = DateTime.UtcNow;
                bool postFailureCapture = (reason ?? string.Empty)
                    .IndexOf("post-failure", StringComparison.OrdinalIgnoreCase) >= 0;
                DateTime cutoff = postFailureCapture
                    && session != null
                    && session.FirstAnomalyUtc != DateTime.MinValue
                    ? session.FirstAnomalyUtc.AddSeconds(-_settings.PreFailureCaptureSeconds)
                    : now.AddSeconds(-_settings.PreFailureCaptureSeconds);
                events = _traceBuffer
                    .Where(item => session == null
                        ? item.TimestampUtc >= cutoff
                        : postFailureCapture
                            ? item.TimestampUtc >= cutoff && item.TimestampUtc <= now
                            : (item.TestSessionId == session.TestId
                                || (item.TimestampUtc >= session.StartedAtUtc.AddSeconds(-_settings.PreFailureCaptureSeconds)
                                    && item.TimestampUtc <= now)))
                    .Where(item => session == null || item.Sequence > session.LastFlushedSequence)
                    .OrderBy(item => item.Sequence)
                    .ToList();
            }

            StringBuilder output = new StringBuilder();
            output.AppendLine(
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + " | EVIDENCE | Reason=" + Sanitize(reason)
                + "; TestId=" + Sanitize(session == null ? string.Empty : session.TestId)
                + "; EventCount=" + events.Count.ToString(CultureInfo.InvariantCulture));
            foreach (LSDeveloperTraceEvent trace in events)
                output.AppendLine(trace.ToTraceLine());

            try
            {
                Directory.CreateDirectory(_paths.LogDirectory);
                File.AppendAllText(_paths.DeveloperTraceLogPath, output.ToString(), Encoding.UTF8);
                TrimTraceFile();
                if (session != null && events.Count > 0)
                    session.LastFlushedSequence = events.Max(item => item.Sequence);
            }
            catch (Exception error)
            {
                LogDebug("DEVELOPER_TRACE_FLUSH_FAILED", error.Message);
            }
        }

        private void TrimTraceFile()
        {
            FileInfo file = new FileInfo(_paths.DeveloperTraceLogPath);
            long maximumBytes = _settings.MaximumTraceFileKilobytes * 1024L;
            if (!file.Exists || file.Length <= maximumBytes)
                return;

            byte[] bytes = File.ReadAllBytes(_paths.DeveloperTraceLogPath);
            int start = Math.Max(0, bytes.Length - (int)maximumBytes);
            while (start < bytes.Length && bytes[start] != (byte)'\n')
                start++;
            if (start < bytes.Length)
                start++;

            string temporary = _paths.DeveloperTraceLogPath
                + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes.Skip(start).ToArray());
                if (File.Exists(_paths.DeveloperTraceLogPath))
                    File.Replace(temporary, _paths.DeveloperTraceLogPath, null);
                else
                    File.Move(temporary, _paths.DeveloperTraceLogPath);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        private LSDeveloperAnalysisResult AnalyzeSession(LSDeveloperTestSession session)
        {
            List<LSDeveloperTraceEvent> events = GetSessionEvents(session)
                .OrderBy(item => item.Sequence)
                .ToList();
            LSDeveloperAnalysisResult result = new LSDeveloperAnalysisResult
            {
                TestId = session.TestId,
                Branch = session.Branch.Name,
                ExpectedBehavior = session.Branch.ExpectedBehavior,
                Events = events
            };

            int expectedIndex = 0;
            LSDeveloperTraceEvent firstProblem = null;
            for (int i = 0; i < events.Count; i++)
            {
                LSDeveloperTraceEvent trace = events[i];
                if (expectedIndex < session.Branch.Steps.Count
                    && session.Branch.Steps[expectedIndex].Matches(trace))
                {
                    result.MatchedSteps.Add(session.Branch.Steps[expectedIndex].Name);
                    expectedIndex++;
                    continue;
                }

                if (firstProblem == null
                    && (trace.EventKind == "TaskConflict"
                        || trace.EventKind == "PhysicalMismatch"
                        || IsSerious(trace)))
                {
                    firstProblem = trace;
                    result.FirstProblemStep = expectedIndex < session.Branch.Steps.Count
                        ? session.Branch.Steps[expectedIndex].Name
                        : "post-branch observation";
                }
            }

            if (firstProblem != null)
            {
                result.FirstDivergence = DescribeFirstProblem(firstProblem, result.FirstProblemStep, session);
                result.Classification = firstProblem.EventKind == "TaskConflict"
                    || firstProblem.EventKind == "PhysicalMismatch"
                    || firstProblem.EventKind == "BehaviorStall"
                    ? "CONFIRMED_ANOMALY"
                    : "CONFIRMED_FAILURE_EVIDENCE";
                result.Inferences.Add("The first divergence is anchored to the earliest serious/conflict/mismatch event in the bounded trace, not the last error.");
            }
            else if (expectedIndex < session.Branch.Steps.Count)
            {
                string missing = session.Branch.Steps[expectedIndex].Name;
                result.FirstDivergence = "Expected step was not confirmed before the test ended or analysis was requested: " + missing + ".";
                result.Classification = session.IsEnded ? "OBSERVED_INCOMPLETE" : "UNKNOWN";
                result.Inferences.Add("The trace contains no serious event proving the cause of the missing step.");
            }
            else
            {
                result.FirstDivergence = "No divergence was observed in the available evidence for the selected branch.";
                result.Classification = "NO_DIVERGENCE_OBSERVED";
            }

            result.ConfirmedFacts.Add("TestId=" + session.TestId + "; Branch=" + session.Branch.Name);
            result.ConfirmedFacts.Add("Trace event count=" + events.Count.ToString(CultureInfo.InvariantCulture) + ".");
            foreach (LSDeveloperTraceEvent trace in events.Where(item => item.EventKind != "ObservedState").Take(30))
                result.ConfirmedFacts.Add("Runtime emitted " + trace.Category + " at " + FormatTime(trace.TimestampUtc) + ".");

            foreach (LSDeveloperTraceEvent trace in LastItems(events.Where(item => !string.IsNullOrWhiteSpace(item.StateAfter)), 20))
                result.ObservedState.Add(
                    trace.Owner + " actor=" + HandleOrUnknown(trace.ActorHandle)
                    + " state=" + ValueOrUnknown(trace.StateBefore) + " -> " + ValueOrUnknown(trace.StateAfter));

            foreach (LSDeveloperTraceEvent trace in LastItems(events.Where(item => !string.IsNullOrWhiteSpace(item.TaskAfter)), 20))
                result.ObservedState.Add(
                    trace.Owner + " actor=" + HandleOrUnknown(trace.ActorHandle)
                    + " task=" + ValueOrUnknown(trace.TaskBefore) + " -> " + ValueOrUnknown(trace.TaskAfter));

            foreach (LSDeveloperTraceEvent trace in events.Where(item => item.EventKind == "TaskConflict"
                || item.EventKind == "PhysicalMismatch"
                || item.EventKind == "BehaviorStall"))
                result.Inferences.Add(DescribeEvent(trace));

            if (!events.Any(item => item.EventKind == "ObservedState"))
                result.Unknown.Add("No selective live actor sample was captured; physical state is unknown.");
            else
                result.Unknown.Add("A last-issued task is observed, but whether GTA kept that task active is unknown unless a physical sample confirms movement or vehicle occupancy.");
            if (events.Any(item => item.EventKind == "BehaviorStall"))
                result.Unknown.Add("Behavioral confirmation proves the sampled logical/physical condition and records the raw task status when available; the exact lower-level GTA reason for non-continuation remains UNKNOWN unless the runtime exposed a rejection, timeout, exception, or task-status explanation.");
            if (events.Any(item => item.ActorHandle <= 0))
                result.Unknown.Add("Some events did not expose an actor handle, so ownership cannot be attributed to an entity for those events.");
            result.EventHistory = LastItems(events, 80).Select(DescribeEvent).ToList();
            result.Summary = "TestId=" + session.TestId
                + "; Branch=" + session.Branch.Name
                + "; Classification=" + result.Classification
                + "; Events=" + events.Count.ToString(CultureInfo.InvariantCulture)
                + "; FirstDivergence=" + result.FirstDivergence;
            return result;
        }

        private string DescribeFirstProblem(
            LSDeveloperTraceEvent problem,
            string expectedStep,
            LSDeveloperTestSession session)
        {
            if (problem.EventKind == "BehaviorStall")
            {
                return "FIRST DIVERGENCE: expected continuation '"
                    + ValueOrUnknown(problem.ExpectedNext)
                    + "' was not confirmed after the selected actor remained in the observed logical/physical condition. "
                    + "This confirms a behavioral stall, not the hidden GTA cause. Evidence="
                    + DescribeEvent(problem);
            }
            if (session.Branch.Id == "fleeing-citizen-interception"
                && expectedStep.IndexOf("custody", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Backup interception did not transition into custody after a valid interception boundary was observed. Evidence="
                    + DescribeEvent(problem);
            }
            if (problem.EventKind == "TaskConflict")
            {
                return "Task ownership conflict occurred before expected step '" + expectedStep
                    + "'. The Developer system reports both owners and does not resolve the conflict. Evidence="
                    + DescribeEvent(problem);
            }
            if (problem.EventKind == "PhysicalMismatch")
            {
                return "Logical/physical mismatch occurred before expected step '" + expectedStep
                    + "'. Evidence=" + DescribeEvent(problem);
            }
            return "Expected step '" + expectedStep + "' first diverged at serious runtime evidence: "
                + DescribeEvent(problem);
        }

        private List<LSDeveloperTraceEvent> GetSessionEvents(LSDeveloperTestSession session)
        {
            Dictionary<long, LSDeveloperTraceEvent> unique = new Dictionary<long, LSDeveloperTraceEvent>();
            foreach (LSDeveloperTraceEvent trace in session.RecentEvents)
                if (trace.TestSessionId == session.TestId)
                    unique[trace.Sequence] = trace;

            foreach (LSDeveloperTraceEvent trace in ReadTraceFile(session.TestId))
                unique[trace.Sequence] = trace;
            return unique.Values.OrderBy(item => item.Sequence).ToList();
        }

        private IEnumerable<LSDeveloperTraceEvent> ReadTraceFile(string testId)
        {
            if (string.IsNullOrWhiteSpace(testId) || !File.Exists(_paths.DeveloperTraceLogPath))
                return Enumerable.Empty<LSDeveloperTraceEvent>();

            List<LSDeveloperTraceEvent> result = new List<LSDeveloperTraceEvent>();
            try
            {
                foreach (string line in File.ReadLines(_paths.DeveloperTraceLogPath))
                {
                    LSDeveloperTraceEvent trace;
                    if (LSDeveloperTraceEvent.TryParseTraceLine(line, out trace)
                        && string.Equals(trace.TestSessionId, testId, StringComparison.Ordinal))
                        result.Add(trace);
                }
            }
            catch (IOException error)
            {
                LogDebug("DEVELOPER_TRACE_READ_FAILED", error.Message);
            }
            return result;
        }

        private void WriteAnalysisReport(LSDeveloperAnalysisResult result)
        {
            if (!_settings.WriteTraceFile || result == null)
                return;

            StringBuilder output = new StringBuilder();
            output.AppendLine("ANALYSIS_REPORT | TestId=" + Sanitize(result.TestId));
            output.AppendLine("CONFIRMED FACT");
            foreach (string line in result.ConfirmedFacts)
                output.AppendLine("- " + Sanitize(line));
            output.AppendLine("OBSERVED STATE");
            foreach (string line in result.ObservedState)
                output.AppendLine("- " + Sanitize(line));
            output.AppendLine("EVENT HISTORY");
            foreach (string line in result.EventHistory)
                output.AppendLine("- " + Sanitize(line));
            output.AppendLine("INFERENCE");
            output.AppendLine("- FIRST DIVERGENCE: " + Sanitize(result.FirstDivergence));
            foreach (string line in result.Inferences)
                output.AppendLine("- " + Sanitize(line));
            output.AppendLine("UNKNOWN");
            foreach (string line in result.Unknown)
                output.AppendLine("- " + Sanitize(line));
            output.AppendLine("FINAL CLASSIFICATION=" + Sanitize(result.Classification));

            try
            {
                File.AppendAllText(_paths.DeveloperTraceLogPath, output.ToString(), Encoding.UTF8);
                TrimTraceFile();
            }
            catch (Exception error)
            {
                LogDebug("DEVELOPER_ANALYSIS_REPORT_WRITE_FAILED", error.Message);
            }
        }

        private void WatchExistingLogs()
        {
            string signature = BuildRuntimeLogSignature();
            if (string.Equals(signature, _lastRuntimeWatchSignature, StringComparison.Ordinal))
                return;
            _lastRuntimeWatchSignature = signature;
            if (_settings.ShowLiveNotifications && signature.IndexOf("Failure", StringComparison.OrdinalIgnoreCase) >= 0)
                Notify("Developer runtime watch detected changed failure evidence. Analyze the active test.");
        }

        private string BuildRuntimeLogSignature()
        {
            List<string> runtime = ReadTail(Path.Combine(_paths.LogDirectory, "LSRuntime.log"));
            List<string> debug = ReadTail(Path.Combine(_paths.LogDirectory, "LSDebug.log"));
            int failures = debug.Count(line => IsSeriousLine(line));
            string last = debug.LastOrDefault(line => IsSeriousLine(line)) ?? string.Empty;
            return "Runtime=" + runtime.Count.ToString(CultureInfo.InvariantCulture)
                + ";Debug=" + debug.Count.ToString(CultureInfo.InvariantCulture)
                + ";Failures=" + failures.ToString(CultureInfo.InvariantCulture)
                + ";Last=" + Limit(last, 320);
        }

        private void ValidateRuntimeLogs()
        {
            try
            {
                string summary = BuildRuntimeLogSignature();
                LogRuntime("DEVELOPER_RUNTIME_LOG_VALIDATION", summary);
                Notify("Runtime log validation: " + summary);
            }
            catch (Exception error)
            {
                LogException("DEVELOPER_RUNTIME_LOG_VALIDATION_FAILED", error);
                Notify("Runtime log validation failed; see LSDebug.log.");
            }
        }

        private List<string> ReadTail(string path)
        {
            List<string> result = new List<string>();
            if (!File.Exists(path))
                return result;

            Queue<string> lines = new Queue<string>();
            int maxBytes = _settings.MaximumTraceFileKilobytes * 1024;
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > maxBytes)
                        stream.Seek(-maxBytes, SeekOrigin.End);
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        if (stream.Position > 0)
                            reader.ReadLine();
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (lines.Count >= _settings.MaximumTraceEvents)
                                lines.Dequeue();
                            lines.Enqueue(line);
                        }
                    }
                }
            }
            catch (IOException error)
            {
                LogDebug("DEVELOPER_RUNTIME_LOG_READ_FAILED", error.Message);
            }
            result.AddRange(lines);
            return result;
        }

        private void ValidatePayload()
        {
            try
            {
                List<string> failures = new List<string>();
                CheckDirectory(failures, _paths.LSImmersiveDirectory, "LSImmersive directory");
                CheckDirectory(failures, _paths.PluginDirectory, "Plugin directory");
                CheckDirectory(failures, _paths.LogDirectory, "Log directory");
                CheckFile(failures, _configurationPath, "LSDeveloper.xml");
                CheckXml(failures, _configurationPath, "LSDeveloper.xml");
                CheckXml(failures, _paths.MainUiXmlPath, "LSImmersiveMainUI.xml");
                CheckXml(failures, _paths.ResponseAudioXmlPath, "ResponseAudio.xml");
                CheckXml(failures, _paths.PoliceProfileXmlPath, "LSPDImmersiveProfile.xml");
                CheckXml(failures, _paths.PoliceUtilityXmlPath, "LSPDImmersiveUtility.xml");
                CheckXml(failures, _paths.PoliceModelPedXmlPath, "LSPoliceModelPed.xml");
                CheckXml(failures, _paths.PoliceVehicleXmlPath, "LSPoliceVehicle.xml");
                CheckXml(failures, _paths.PoliceWeaponDataXmlPath, "LSPoliceWeaponData.xml");
                CheckXml(failures, _paths.PolicePersonalWeaponXmlPath, "LSPolicePersonalWeapons.xml");
                CheckXml(failures, _paths.NpcDatabaseXmlPath, "LSNPCDatabase.xml");
                CheckXml(failures, _paths.DispatchEventXmlPath, "LSPDDispatchEvent.xml");
                CheckXml(failures, _paths.CrimeActivityEventXmlPath, "LSPDCrimeActivityEvent.xml");
                CheckXml(failures, _paths.CriminalProfileXmlPath, "LSPDCriminalProfile.xml");

                if (!IsUnder(_configurationPath, _paths.PluginDirectory))
                    failures.Add("LSDeveloper.xml is outside the Plugin directory.");

                string summary = "Checks=" + (failures.Count == 0 ? "passed" : "review findings")
                    + "; FindingCount=" + failures.Count.ToString(CultureInfo.InvariantCulture);
                LogRuntime("DEVELOPER_PAYLOAD_VALIDATION", summary);
                foreach (string failure in failures)
                    LogDebug("DEVELOPER_PAYLOAD_FINDING", failure);
                Notify(failures.Count == 0
                    ? "Developer XML/path/payload validation passed."
                    : "Developer payload validation found " + failures.Count + " issue(s); see LSDebug.log.");
            }
            catch (Exception error)
            {
                LogException("DEVELOPER_PAYLOAD_VALIDATION_FAILED", error);
                Notify("Developer payload validation failed; see LSDebug.log.");
            }
        }

        private void CheckDirectory(List<string> failures, string path, string label)
        {
            if (!Directory.Exists(path))
                failures.Add(label + " is missing: " + path);
        }

        private void CheckFile(List<string> failures, string path, string label)
        {
            if (!File.Exists(path))
                failures.Add(label + " is missing: " + path);
        }

        private void CheckXml(List<string> failures, string path, string label)
        {
            if (!File.Exists(path))
                return;
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };
                using (XmlReader reader = XmlReader.Create(path, settings))
                    XDocument.Load(reader, LoadOptions.None);
            }
            catch (Exception error)
            {
                failures.Add(label + " is invalid or unsafe: " + Limit(error.Message, 280));
            }
        }

        private void SaveConfiguration()
        {
            try
            {
                _settings.Normalize();
                _settings.Save(_configurationPath);
                LogRuntime("DEVELOPER_CONFIGURATION_SAVED", "Saved=" + _configurationPath);
                Notify("Developer configuration saved. Gameplay configuration was not changed.");
            }
            catch (Exception error)
            {
                LogException("DEVELOPER_CONFIGURATION_SAVE_FAILED", error);
                Notify("Developer configuration could not be saved.");
            }
        }

        private void ReloadConfiguration()
        {
            try
            {
                LSDeveloperSettings loaded = LSDeveloperSettings.Load(
                    _configurationPath,
                    delegate(string message) { LogDebug("DEVELOPER_CONFIG", message); });
                _settings.CopyFrom(loaded);
                _nextSampleUtc = DateTime.MinValue;
                _nextRuntimeWatchUtc = DateTime.MinValue;
                LogRuntime("DEVELOPER_CONFIGURATION_RELOADED", "Reloaded=" + _configurationPath);
                Notify("Developer configuration reloaded. Gameplay was not reloaded or reset.");
            }
            catch (Exception error)
            {
                LogException("DEVELOPER_CONFIGURATION_RELOAD_FAILED", error);
                Notify("Developer configuration could not be reloaded.");
            }
        }

        private void AddHeader(string title)
        {
            NativeItem header = new NativeItem(title);
            header.Enabled = false;
            Menu.Add(header);
        }

        private void AddAction(string title, string description, Action action)
        {
            NativeItem item = new NativeItem(title, description);
            item.Activated += delegate
            {
                if (_settings.Enabled && _settings.DeveloperMenuEnabled && action != null)
                    action();
                else
                    Notify("Developer Mode is disabled in LSDeveloper.xml.");
            };
            Menu.Add(item);
        }

        private void Notify(string message)
        {
            if (!_settings.ShowLiveNotifications)
                return;
            Notification.PostTicker(Limit(message, 700), false, false);
        }

        private void LogRuntime(string category, string message)
        {
            if (_log != null)
                _log.Runtime(category, message);
        }

        private void LogDebug(string category, string message)
        {
            if (_log != null)
                _log.Debug(category, message);
        }

        private void LogException(string category, Exception error)
        {
            if (_log != null)
                _log.Exception(category, error);
        }

        private static bool IsSerious(LSDeveloperTraceEvent trace)
        {
            return trace != null && (trace.EventKind == "Failure"
                || trace.EventKind == "TaskConflict"
                || trace.EventKind == "PhysicalMismatch"
                || trace.EventKind == "BehaviorStall"
                || trace.EventKind == "TargetFailure"
                || trace.Category.IndexOf("FAIL", StringComparison.OrdinalIgnoreCase) >= 0
                || trace.Category.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0
                || trace.Category.IndexOf("EXCEPTION", StringComparison.OrdinalIgnoreCase) >= 0
                || trace.Category.IndexOf("INVALID", StringComparison.OrdinalIgnoreCase) >= 0
                || trace.Category.IndexOf("UNAVAILABLE", StringComparison.OrdinalIgnoreCase) >= 0
                || trace.Category.IndexOf("TIMEOUT", StringComparison.OrdinalIgnoreCase) >= 0
                || trace.Category.IndexOf("REJECTED", StringComparison.OrdinalIgnoreCase) >= 0
                || trace.Category.IndexOf("LOST", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsSeriousLine(string line)
        {
            return !string.IsNullOrWhiteSpace(line)
                && (line.IndexOf("FAIL", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("EXCEPTION", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("INVALID", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("UNAVAILABLE", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("TIMEOUT", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("REJECTED", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("LOST", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool ShouldObserveLog(string category, string level)
        {
            string value = (category ?? string.Empty).ToUpperInvariant();
            string severity = (level ?? string.Empty).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(value))
                return false;
            if (severity.Contains("ERROR") || severity.Contains("EXCEPTION") || severity.Contains("FAIL"))
                return true;

            // These are known high-frequency ambient/status records. They do
            // not describe the controlled actor's decision boundary and would
            // otherwise evict useful pre-anomaly evidence from the bounded
            // buffer while a test is running.
            if (value == "AUDIO_PLAYED"
                || value == "LOG_REPEAT_SUMMARY"
                || value.Contains("TRAFFIC_COLLISION_GUARD")
                || value.Contains("ACTIVE_SCENE_")
                || value.Contains("GANG_AWARENESS_")
                || value.Contains("GANG_THREATS_TRACKED")
                || value.Contains("AUTHORITY_WANTED_CAP"))
                return false;

            if (value.Contains("DEVELOPER")
                || value.Contains("NPC_")
                || value.Contains("BACKUP")
                || value.Contains("DISPATCH")
                || value.Contains("CONVOY")
                || value.Contains("CUSTODY")
                || value.Contains("CRIME_ACTIVITY")
                || value.Contains("GANG_INCIDENT")
                || value.Contains("GANG_RESPONSE")
                || value.Contains("PATROL")
                || value.Contains("TASK")
                || value.Contains("STATE")
                || value.Contains("PHASE")
                || value.Contains("TARGET")
                || value.Contains("ROUTE")
                || value.Contains("ESCORT")
                || value.Contains("TRANSPORT")
                || value.Contains("VEHICLE")
                || value.Contains("DOOR")
                || value.Contains("STATION")
                || value.Contains("HANDOFF")
                || value.Contains("CLEANUP")
                || value.Contains("ASSET")
                || value.Contains("MODEL")
                || value.Contains("INTERCEPT")
                || value.Contains("CONTAIN")
                || value.Contains("ARREST")
                || value.Contains("FLEE")
                || value.Contains("DOCUMENT")
                || value.Contains("COMPLI")
                || value.Contains("RESIST")
                || value.Contains("SURRENDER")
                || value.Contains("OWNERSHIP")
                || value.Contains("TIMEOUT")
                || value.Contains("INVALID")
                || value.Contains("UNAVAILABLE")
                || value.Contains("REJECT")
                || value.Contains("ARRIV")
                || value.Contains("FAIL")
                || value.Contains("ERROR"))
                return true;

            return false;
        }

        private static string DescribeEvent(LSDeveloperTraceEvent trace)
        {
            if (trace == null)
                return "unknown event";
            return FormatTime(trace.TimestampUtc)
                + " | " + trace.EventKind
                + " | Owner=" + ValueOrUnknown(trace.Owner)
                + " | Category=" + trace.Category
                + " | Actor=" + HandleOrUnknown(trace.ActorHandle)
                + " | Target=" + HandleOrUnknown(trace.TargetHandle)
                + " | Vehicle=" + HandleOrUnknown(trace.VehicleHandle)
                + " | State=" + ValueOrUnknown(trace.StateBefore) + "->" + ValueOrUnknown(trace.StateAfter)
                + " | Task=" + ValueOrUnknown(trace.TaskBefore) + "->" + ValueOrUnknown(trace.TaskAfter)
                + (string.IsNullOrWhiteSpace(trace.PhysicalBehavior)
                    ? string.Empty : " | Physical=" + trace.PhysicalBehavior)
                + (string.IsNullOrWhiteSpace(trace.TaskStatus)
                    ? string.Empty : " | TaskStatus=" + trace.TaskStatus)
                + (string.IsNullOrWhiteSpace(trace.TaskStatus)
                    || trace.TaskStatus.IndexOf("TASK_ARREST_PED", StringComparison.OrdinalIgnoreCase) < 0
                    ? string.Empty : " | ArrestTaskNative=" + trace.IsRunningArrestTask)
                + (string.IsNullOrWhiteSpace(trace.ExpectedNext)
                    ? string.Empty : " | ExpectedNext=" + Limit(trace.ExpectedNext, 220))
                + (string.IsNullOrWhiteSpace(trace.SourceFile)
                    ? string.Empty : " | Logic=" + trace.SourceFile + "::" + ValueOrUnknown(trace.SourceMember))
                + (float.IsNaN(trace.Distance) ? string.Empty
                    : " | Distance=" + trace.Distance.ToString("0.0", CultureInfo.InvariantCulture))
                + (float.IsNaN(trace.TargetDistance) ? string.Empty
                    : " | TargetDistance=" + trace.TargetDistance.ToString("0.0", CultureInfo.InvariantCulture))
                + (float.IsNaN(trace.MovementDelta) ? string.Empty
                    : " | MovementDelta=" + trace.MovementDelta.ToString("0.0", CultureInfo.InvariantCulture))
                + (trace.HasPosition ? " | Position=" + trace.Position : string.Empty)
                + (trace.CurrentWeaponHash == 0 ? string.Empty
                    : " | WeaponHash=" + trace.CurrentWeaponHash.ToString(CultureInfo.InvariantCulture))
                + (trace.CombatTargetHandle <= 0 ? string.Empty
                    : " | CombatTarget=" + trace.CombatTargetHandle.ToString(CultureInfo.InvariantCulture))
                + (string.IsNullOrWhiteSpace(trace.Reason) ? string.Empty : " | Reason=" + Limit(trace.Reason, 240));
        }

        private static string FormatTime(DateTime utc)
        {
            return utc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        private static string ValueOrUnknown(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "UNKNOWN" : value;
        }

        private static string HandleOrUnknown(int handle)
        {
            return handle <= 0 ? "UNKNOWN" : handle.ToString(CultureInfo.InvariantCulture);
        }

        private static string Limit(string value, int length)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= length)
                return value ?? string.Empty;
            return value.Substring(0, length) + "...";
        }

        private static List<T> LastItems<T>(IEnumerable<T> source, int count)
        {
            Queue<T> queue = new Queue<T>();
            if (source == null || count <= 0)
                return queue.ToList();

            foreach (T item in source)
            {
                if (queue.Count >= count)
                    queue.Dequeue();
                queue.Enqueue(item);
            }
            return queue.ToList();
        }

        private static string Sanitize(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace(";", ",");
        }

        private static bool IsUnder(string path, string directory)
        {
            try
            {
                string root = Path.GetFullPath(directory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Static passive bridge used by the existing logger and central state
    /// setters. It contains no gameplay command and has no entity ownership.
    /// </summary>
    internal static class LSDeveloperRuntime
    {
        private static readonly object Sync = new object();
        private static LSImmersiveDeveloper _current;

        internal static void Attach(LSImmersiveDeveloper developer)
        {
            lock (Sync)
                _current = developer;
        }

        internal static void Detach(LSImmersiveDeveloper developer)
        {
            lock (Sync)
            {
                if (ReferenceEquals(_current, developer))
                    _current = null;
            }
        }

        internal static void ObserveLog(
            string runtimeSessionId,
            string level,
            string category,
            string message,
            DateTime timestampUtc)
        {
            LSImmersiveDeveloper developer;
            lock (Sync)
                developer = _current;
            if (developer != null)
            {
                try
                {
                    developer.ObserveLog(runtimeSessionId, level, category, message, timestampUtc);
                }
                catch
                {
                    // Developer observation must never interrupt the existing log boundary.
                }
            }
        }

        internal static void StateTransition(
            string runtimeSessionId,
            string owner,
            string category,
            string before,
            string after,
            int actorHandle,
            int targetHandle,
            int vehicleHandle,
            string reason)
        {
            LSImmersiveDeveloper developer;
            lock (Sync)
                developer = _current;
            if (developer != null)
            {
                try
                {
                    developer.ObserveExplicitState(
                        runtimeSessionId,
                        owner,
                        category,
                        before,
                        after,
                        actorHandle,
                        targetHandle,
                        vehicleHandle,
                        reason,
                        DateTime.UtcNow);
                }
                catch
                {
                    // Developer observation must never change a gameplay transition.
                }
            }
        }

        internal static void TaskIssued(
            string runtimeSessionId,
            string owner,
            string task,
            int actorHandle,
            int targetHandle,
            int vehicleHandle,
            string reason)
        {
            LSImmersiveDeveloper developer;
            lock (Sync)
                developer = _current;
            if (developer != null)
            {
                try
                {
                    developer.ObserveExplicitTask(
                        runtimeSessionId,
                        owner,
                        task,
                        actorHandle,
                        targetHandle,
                        vehicleHandle,
                        reason,
                        DateTime.UtcNow);
                }
                catch
                {
                    // Developer observation must never choose or replace a task owner.
                }
            }
        }
    }

    internal enum LSDeveloperTraceMode
    {
        Off,
        Test,
        Always
    }

    internal sealed class LSDeveloperSettings
    {
        internal bool Enabled { get; set; }
        internal bool DeveloperMenuEnabled { get; set; }
        internal bool AutomaticRuntimeWatch { get; set; }
        internal bool AutomaticAnomalyDetection { get; set; }
        internal bool LiveBehavioralConfirmation { get; set; }
        internal LSDeveloperTraceMode TraceMode { get; set; }
        internal int SamplingIntervalMilliseconds { get; set; }
        internal int BehavioralStallSeconds { get; set; }
        internal int TraceBufferSeconds { get; set; }
        internal int MaximumTraceEvents { get; set; }
        internal int MaximumTraceFileKilobytes { get; set; }
        internal int PreFailureCaptureSeconds { get; set; }
        internal int PostFailureCaptureSeconds { get; set; }
        internal bool ShowLiveNotifications { get; set; }
        internal bool ShowDetailedNotifications { get; set; }
        internal bool WriteLifecycleMarkers { get; set; }
        internal bool WriteTraceFile { get; set; }

        private LSDeveloperSettings()
        {
            Enabled = true;
            DeveloperMenuEnabled = true;
            AutomaticRuntimeWatch = false;
            AutomaticAnomalyDetection = true;
            LiveBehavioralConfirmation = true;
            TraceMode = LSDeveloperTraceMode.Test;
            SamplingIntervalMilliseconds = 750;
            BehavioralStallSeconds = 6;
            TraceBufferSeconds = 30;
            MaximumTraceEvents = 800;
            MaximumTraceFileKilobytes = 2048;
            PreFailureCaptureSeconds = 8;
            PostFailureCaptureSeconds = 5;
            ShowLiveNotifications = true;
            ShowDetailedNotifications = false;
            WriteLifecycleMarkers = true;
            WriteTraceFile = true;
        }

        internal static LSDeveloperSettings Load(string path, Action<string> debug)
        {
            LSDeveloperSettings result = new LSDeveloperSettings();
            try
            {
                if (!File.Exists(path))
                    return result;

                XElement root;
                XmlReaderSettings settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };
                using (XmlReader reader = XmlReader.Create(path, settings))
                    root = XElement.Load(reader, LoadOptions.None);
                if (root.Name != "LSDeveloper")
                    throw new InvalidDataException("Unexpected LSDeveloper.xml root.");

                result.Enabled = Bool(root, "enabled", result.Enabled);
                result.DeveloperMenuEnabled = Bool(root, "developerMenuEnabled", result.DeveloperMenuEnabled);
                result.AutomaticRuntimeWatch = Bool(
                    root,
                    "automaticRuntimeWatch",
                    Bool(root, "autoWatchRuntimeLog", result.AutomaticRuntimeWatch));
                result.AutomaticAnomalyDetection = Bool(root, "automaticAnomalyDetection", result.AutomaticAnomalyDetection);
                result.LiveBehavioralConfirmation = Bool(
                    root,
                    "liveBehavioralConfirmation",
                    result.LiveBehavioralConfirmation);
                result.TraceMode = ParseTraceMode(Attr(root, "traceMode"), result.TraceMode);
                result.SamplingIntervalMilliseconds = Int(root, "samplingIntervalMilliseconds", result.SamplingIntervalMilliseconds, 250, 10000);
                result.BehavioralStallSeconds = Int(root, "behavioralStallSeconds", result.BehavioralStallSeconds, 3, 30);
                result.TraceBufferSeconds = Int(root, "traceBufferSeconds", result.TraceBufferSeconds, 5, 300);
                result.MaximumTraceEvents = Int(root, "maximumTraceEvents", result.MaximumTraceEvents, 100, 5000);
                result.MaximumTraceFileKilobytes = Int(root, "maximumTraceFileKilobytes", result.MaximumTraceFileKilobytes, 256, 8192);
                result.PreFailureCaptureSeconds = Int(root, "preFailureCaptureSeconds", result.PreFailureCaptureSeconds, 1, 60);
                result.PostFailureCaptureSeconds = Int(root, "postFailureCaptureSeconds", result.PostFailureCaptureSeconds, 0, 60);
                result.ShowLiveNotifications = Bool(root, "showLiveNotifications", result.ShowLiveNotifications);
                result.ShowDetailedNotifications = Bool(root, "showDetailedNotifications", result.ShowDetailedNotifications);
                result.WriteLifecycleMarkers = Bool(root, "writeLifecycleMarkers", result.WriteLifecycleMarkers);
                result.WriteTraceFile = Bool(root, "writeTraceFile", result.WriteTraceFile);
                result.Normalize();
            }
            catch (Exception error)
            {
                if (debug != null)
                    debug(error.ToString());
            }
            return result;
        }

        internal void CopyFrom(LSDeveloperSettings source)
        {
            if (source == null)
                return;
            Enabled = source.Enabled;
            DeveloperMenuEnabled = source.DeveloperMenuEnabled;
            AutomaticRuntimeWatch = source.AutomaticRuntimeWatch;
            AutomaticAnomalyDetection = source.AutomaticAnomalyDetection;
            LiveBehavioralConfirmation = source.LiveBehavioralConfirmation;
            TraceMode = source.TraceMode;
            SamplingIntervalMilliseconds = source.SamplingIntervalMilliseconds;
            BehavioralStallSeconds = source.BehavioralStallSeconds;
            TraceBufferSeconds = source.TraceBufferSeconds;
            MaximumTraceEvents = source.MaximumTraceEvents;
            MaximumTraceFileKilobytes = source.MaximumTraceFileKilobytes;
            PreFailureCaptureSeconds = source.PreFailureCaptureSeconds;
            PostFailureCaptureSeconds = source.PostFailureCaptureSeconds;
            ShowLiveNotifications = source.ShowLiveNotifications;
            ShowDetailedNotifications = source.ShowDetailedNotifications;
            WriteLifecycleMarkers = source.WriteLifecycleMarkers;
            WriteTraceFile = source.WriteTraceFile;
            Normalize();
        }

        internal void Normalize()
        {
            SamplingIntervalMilliseconds = Clamp(SamplingIntervalMilliseconds, 250, 10000);
            BehavioralStallSeconds = Clamp(BehavioralStallSeconds, 3, 30);
            TraceBufferSeconds = Clamp(TraceBufferSeconds, 5, 300);
            MaximumTraceEvents = Clamp(MaximumTraceEvents, 100, 5000);
            MaximumTraceFileKilobytes = Clamp(MaximumTraceFileKilobytes, 256, 8192);
            PreFailureCaptureSeconds = Clamp(PreFailureCaptureSeconds, 1, 60);
            PostFailureCaptureSeconds = Clamp(PostFailureCaptureSeconds, 0, 60);
        }

        internal void Save(string path)
        {
            Normalize();
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            XDocument document = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(
                    "LSDeveloper",
                    new XAttribute("version", "2.0"),
                    new XAttribute("enabled", Enabled),
                    new XAttribute("developerMenuEnabled", DeveloperMenuEnabled),
                    new XAttribute("automaticRuntimeWatch", AutomaticRuntimeWatch),
                    new XAttribute("automaticAnomalyDetection", AutomaticAnomalyDetection),
                    new XAttribute("liveBehavioralConfirmation", LiveBehavioralConfirmation),
                    new XAttribute("traceMode", TraceMode),
                    new XAttribute("samplingIntervalMilliseconds", SamplingIntervalMilliseconds),
                    new XAttribute("behavioralStallSeconds", BehavioralStallSeconds),
                    new XAttribute("traceBufferSeconds", TraceBufferSeconds),
                    new XAttribute("maximumTraceEvents", MaximumTraceEvents),
                    new XAttribute("maximumTraceFileKilobytes", MaximumTraceFileKilobytes),
                    new XAttribute("preFailureCaptureSeconds", PreFailureCaptureSeconds),
                    new XAttribute("postFailureCaptureSeconds", PostFailureCaptureSeconds),
                    new XAttribute("showLiveNotifications", ShowLiveNotifications),
                    new XAttribute("showDetailedNotifications", ShowDetailedNotifications),
                    new XAttribute("writeLifecycleMarkers", WriteLifecycleMarkers),
                    new XAttribute("writeTraceFile", WriteTraceFile)));
            document.Save(path);
        }

        internal static LSDeveloperTraceMode ParseTraceMode(string value, LSDeveloperTraceMode fallback)
        {
            LSDeveloperTraceMode result;
            return Enum.TryParse(value, true, out result) ? result : fallback;
        }

        private static string Attr(XElement node, string name)
        {
            XAttribute attribute = node == null ? null : node.Attribute(name);
            return attribute == null ? null : attribute.Value;
        }

        private static bool Bool(XElement node, string name, bool fallback)
        {
            bool result;
            return bool.TryParse(Attr(node, name), out result) ? result : fallback;
        }

        private static int Int(XElement node, string name, int fallback, int minimum, int maximum)
        {
            int result;
            return int.TryParse(Attr(node, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
                ? Clamp(result, minimum, maximum) : fallback;
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }

    internal sealed class LSDeveloperTraceEvent
    {
        internal LSDeveloperTraceEvent()
        {
            Distance = float.NaN;
            TargetDistance = float.NaN;
            MovementDelta = float.NaN;
        }

        internal long Sequence { get; set; }
        internal DateTime TimestampUtc { get; set; }
        internal string RuntimeSessionId { get; set; }
        internal string TestSessionId { get; set; }
        internal string Owner { get; set; }
        internal string Category { get; set; }
        internal string EventKind { get; set; }
        internal int ActorHandle { get; set; }
        internal string ActorModel { get; set; }
        internal int TargetHandle { get; set; }
        internal string TargetModel { get; set; }
        internal int VehicleHandle { get; set; }
        internal string VehicleModel { get; set; }
        internal string StateBefore { get; set; }
        internal string StateAfter { get; set; }
        internal string TaskBefore { get; set; }
        internal string TaskAfter { get; set; }
        internal string Reason { get; set; }
        internal string ExpectedNext { get; set; }
        internal string SourceFile { get; set; }
        internal string SourceMember { get; set; }
        internal string PhysicalBehavior { get; set; }
        internal string TaskStatus { get; set; }
        internal int CurrentWeaponHash { get; set; }
        internal bool IsShooting { get; set; }
        internal bool IsInCombat { get; set; }
        internal bool IsAimingFromCover { get; set; }
        internal bool IsRunningArrestTask { get; set; }
        internal int CombatTargetHandle { get; set; }
        internal float Distance { get; set; }
        internal float TargetDistance { get; set; }
        internal float MovementDelta { get; set; }
        internal bool HasPosition { get; set; }
        internal Vector3 Position { get; set; }
        internal string Context { get; set; }

        internal static LSDeveloperTraceEvent FromLog(
            string runtimeSessionId,
            string level,
            string category,
            string message,
            DateTime timestampUtc)
        {
            Dictionary<string, string> fields = ParseFields(message);
            Vector3 position;
            bool hasPosition = TryParsePosition(
                Value(fields, "Position", "Spawn", "Location", "Origin", "RoadPosition", "Destination"),
                out position);
            LSDeveloperTraceEvent trace = new LSDeveloperTraceEvent
            {
                TimestampUtc = timestampUtc,
                RuntimeSessionId = runtimeSessionId ?? string.Empty,
                Owner = InferOwner(category),
                Category = category ?? "UNCLASSIFIED_EVENT",
                EventKind = InferKind(category, message, level),
                ActorHandle = Handle(fields, "Actor", "Ped", "Driver", "Subject", "Prisoner", "Officer", "Suspect"),
                ActorModel = Value(fields, "ActorModel", "PedModel", "Model", "PedAsset"),
                TargetHandle = Handle(fields, "Target", "TargetPed", "Suspect"),
                TargetModel = Value(fields, "TargetModel", "TargetAsset"),
                VehicleHandle = Handle(fields, "Vehicle", "SubjectVehicle", "BackupVehicle", "Transport"),
                VehicleModel = Value(fields, "VehicleModel", "VehicleAsset"),
                StateBefore = Value(fields, "StateBefore", "PreviousState", "BeforeState"),
                StateAfter = Value(fields, "StateAfter", "State", "Stage", "Phase"),
                TaskBefore = Value(fields, "TaskBefore", "PreviousTask", "BeforeTask"),
                TaskAfter = Value(fields, "TaskAfter", "Task", "TaskName", "Action"),
                Reason = Value(fields, "Reason", "FailureReason"),
                ExpectedNext = Value(fields, "ExpectedNext"),
                SourceFile = Value(fields, "LogicFile", "SourceFile"),
                SourceMember = Value(fields, "LogicMember", "SourceMember"),
                PhysicalBehavior = Value(fields, "PhysicalBehavior"),
                TaskStatus = Value(fields, "TaskStatus"),
                CurrentWeaponHash = Handle(fields, "WeaponHash"),
                IsShooting = Boolean(fields, "Shooting"),
                IsInCombat = Boolean(fields, "InCombat"),
                IsAimingFromCover = Boolean(fields, "AimingFromCover"),
                IsRunningArrestTask = Boolean(fields, "ArrestTaskNative"),
                CombatTargetHandle = Handle(fields, "CombatTarget"),
                Distance = Number(fields, "Distance", "VehicleDistance", "TargetDistance"),
                TargetDistance = Number(fields, "TargetDistance"),
                MovementDelta = Number(fields, "MovementDelta"),
                HasPosition = hasPosition,
                Position = position,
                Context = message ?? string.Empty
            };

            if (string.IsNullOrWhiteSpace(trace.StateAfter))
                trace.StateAfter = InferState(category, message);
            if (string.IsNullOrWhiteSpace(trace.TaskAfter)
                && (trace.EventKind == "TaskIssued" || category.IndexOf("TASK", StringComparison.OrdinalIgnoreCase) >= 0))
                trace.TaskAfter = category;
            if (string.IsNullOrWhiteSpace(trace.Reason))
                trace.Reason = Value(fields, "Fallback", "Action", "Result");
            if (string.IsNullOrWhiteSpace(trace.SourceFile))
                trace.SourceFile = InferSourceFile(trace.Category, trace.Owner);
            if (string.IsNullOrWhiteSpace(trace.SourceMember))
                trace.SourceMember = InferSourceMember(trace.Category, trace.Owner);
            return trace;
        }

        internal string ToTraceLine()
        {
            return TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + " | TRACE | "
                + "Sequence=" + Sequence.ToString(CultureInfo.InvariantCulture)
                + ";RuntimeSessionId=" + Sanitize(RuntimeSessionId)
                + ";TestId=" + Sanitize(TestSessionId)
                + ";Owner=" + Sanitize(Owner)
                + ";Kind=" + Sanitize(EventKind)
                + ";Category=" + Sanitize(Category)
                + ";Actor=" + ActorHandle.ToString(CultureInfo.InvariantCulture)
                + ";ActorModel=" + Sanitize(ActorModel)
                + ";Target=" + TargetHandle.ToString(CultureInfo.InvariantCulture)
                + ";TargetModel=" + Sanitize(TargetModel)
                + ";Vehicle=" + VehicleHandle.ToString(CultureInfo.InvariantCulture)
                + ";VehicleModel=" + Sanitize(VehicleModel)
                + ";StateBefore=" + Sanitize(StateBefore)
                 + ";StateAfter=" + Sanitize(StateAfter)
                 + ";TaskBefore=" + Sanitize(TaskBefore)
                 + ";TaskAfter=" + Sanitize(TaskAfter)
                 + ";ExpectedNext=" + Sanitize(ExpectedNext)
                 + ";LogicFile=" + Sanitize(SourceFile)
                 + ";LogicMember=" + Sanitize(SourceMember)
                 + ";PhysicalBehavior=" + Sanitize(PhysicalBehavior)
                 + ";TaskStatus=" + Sanitize(TaskStatus)
                 + ";WeaponHash=" + CurrentWeaponHash.ToString(CultureInfo.InvariantCulture)
                 + ";Shooting=" + IsShooting
                 + ";InCombat=" + IsInCombat
                 + ";AimingFromCover=" + IsAimingFromCover
                 + ";ArrestTaskNative=" + IsRunningArrestTask
                 + ";CombatTarget=" + CombatTargetHandle.ToString(CultureInfo.InvariantCulture)
                 + ";Reason=" + Sanitize(Reason)
                 + ";Distance=" + (float.IsNaN(Distance) ? string.Empty : Distance.ToString("0.###", CultureInfo.InvariantCulture))
                 + ";TargetDistance=" + (float.IsNaN(TargetDistance) ? string.Empty : TargetDistance.ToString("0.###", CultureInfo.InvariantCulture))
                 + ";MovementDelta=" + (float.IsNaN(MovementDelta) ? string.Empty : MovementDelta.ToString("0.###", CultureInfo.InvariantCulture))
                 + ";Position=" + (HasPosition ? Sanitize(Position.ToString()) : string.Empty)
                 + ";Context=" + Sanitize(Context);
        }

        internal static bool TryParseTraceLine(string line, out LSDeveloperTraceEvent trace)
        {
            trace = null;
            if (string.IsNullOrWhiteSpace(line) || line.IndexOf(" | TRACE | ", StringComparison.Ordinal) < 0)
                return false;
            int marker = line.IndexOf(" | TRACE | ", StringComparison.Ordinal);
            string timestamp = line.Substring(0, marker);
            string payload = line.Substring(marker + " | TRACE | ".Length);
            DateTime parsed;
            Dictionary<string, string> fields = ParseFields(payload);
            Vector3 position;
            bool hasPosition = TryParsePosition(Value(fields, "Position"), out position);
            if (!DateTime.TryParseExact(timestamp, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
                parsed = DateTime.Now;
            trace = new LSDeveloperTraceEvent
            {
                TimestampUtc = parsed.ToUniversalTime(),
                Sequence = Long(fields, "Sequence"),
                RuntimeSessionId = Value(fields, "RuntimeSessionId"),
                TestSessionId = Value(fields, "TestId"),
                Owner = Value(fields, "Owner"),
                EventKind = Value(fields, "Kind"),
                Category = Value(fields, "Category"),
                ActorHandle = Handle(fields, "Actor"),
                ActorModel = Value(fields, "ActorModel"),
                TargetHandle = Handle(fields, "Target"),
                TargetModel = Value(fields, "TargetModel"),
                VehicleHandle = Handle(fields, "Vehicle"),
                VehicleModel = Value(fields, "VehicleModel"),
                StateBefore = Value(fields, "StateBefore"),
                StateAfter = Value(fields, "StateAfter"),
                TaskBefore = Value(fields, "TaskBefore"),
                TaskAfter = Value(fields, "TaskAfter"),
                ExpectedNext = Value(fields, "ExpectedNext"),
                SourceFile = Value(fields, "LogicFile", "SourceFile"),
                SourceMember = Value(fields, "LogicMember", "SourceMember"),
                PhysicalBehavior = Value(fields, "PhysicalBehavior"),
                TaskStatus = Value(fields, "TaskStatus"),
                CurrentWeaponHash = Handle(fields, "WeaponHash"),
                IsShooting = Boolean(fields, "Shooting"),
                IsInCombat = Boolean(fields, "InCombat"),
                IsAimingFromCover = Boolean(fields, "AimingFromCover"),
                IsRunningArrestTask = Boolean(fields, "ArrestTaskNative"),
                CombatTargetHandle = Handle(fields, "CombatTarget"),
                Reason = Value(fields, "Reason"),
                Distance = Number(fields, "Distance"),
                TargetDistance = Number(fields, "TargetDistance"),
                MovementDelta = Number(fields, "MovementDelta"),
                HasPosition = hasPosition,
                Position = position,
                Context = Value(fields, "Context")
            };
            if (string.IsNullOrWhiteSpace(trace.SourceFile))
                trace.SourceFile = InferSourceFile(trace.Category, trace.Owner);
            if (string.IsNullOrWhiteSpace(trace.SourceMember))
                trace.SourceMember = InferSourceMember(trace.Category, trace.Owner);
            return trace.Sequence > 0 && !string.IsNullOrWhiteSpace(trace.Category);
        }

        internal static string InferSourceFile(string category, string owner)
        {
            string value = (category ?? string.Empty).ToUpperInvariant();
            string ownerValue = (owner ?? string.Empty).ToUpperInvariant();
            if (value.Contains("NPC") || ownerValue == "NPCRESPONSE")
                return "LSPDNPCResponse.cs";
            if (value.Contains("BACKUP") || ownerValue == "BACKUP")
                return "LSPDBackUp.cs";
            if (value.Contains("CONVOY") || value.Contains("CUSTODY") || ownerValue == "CONVOY")
                return "LSPDConvoy.cs";
            if (value.Contains("DISPATCH") || ownerValue == "DISPATCH")
                return "LSPDDispatch.cs";
            if (value.Contains("CRIME_ACTIVITY") || ownerValue == "CRIMEACTIVITY")
                return "LSPDCrimeActivity.cs";
            if (value.Contains("AUTHORITY") || ownerValue == "POLICEAUTHORITY")
                return "LSPDAuthority.cs";
            if (value.Contains("RESPONSE") || ownerValue == "POLICERESPONSE")
                return "LSPDPoliceResponse.cs";
            if (value.Contains("DEVELOPER") || ownerValue == "DEVELOPER")
                return "LSImmersiveDeveloper.cs";
            return string.Empty;
        }

        internal static string InferSourceMember(string category, string owner)
        {
            string value = (category ?? string.Empty).ToUpperInvariant();
            string ownerValue = (owner ?? string.Empty).ToUpperInvariant();
            if (value == "NPC_INTERACTION_STATE")
                return "LSPDNPCResponse._stage setter / InteractionStage";
            if (value == "POLICE_BACKUP_STATE")
                return "LSPDBackUp.SetState";
            if (value == "POLICE_DISPATCH_STATE")
                return "LSPDDispatch.SetState";
            if (value == "POLICE_CRIME_ACTIVITY_STATE")
                return "LSPDCrimeActivity.SetState";
            if (value == "POLICE_CONVOY_PHASE")
                return "LSPDConvoy.SetPhase";
            if (value == "TASK_ISSUED")
                return ownerValue == "NPCRESPONSE"
                    ? "LSPDNPCResponse task issuance callsite"
                    : "existing gameplay task issuance callsite";
            if (value.Contains("NPC_BACKUP"))
                return "LSPDNPCResponse.AdvanceBackupHandoff";
            if (value.Contains("NPC_"))
                return "LSPDNPCResponse interaction flow";
            if (value.Contains("BACKUP"))
                return "LSPDBackUp backup flow";
            if (value.Contains("CONVOY") || value.Contains("CUSTODY"))
                return "LSPDConvoy custody flow";
            if (value.Contains("DISPATCH"))
                return "LSPDDispatch dispatch flow";
            if (value.Contains("CRIME_ACTIVITY"))
                return "LSPDCrimeActivity crime activity flow";
            if (value.Contains("DEVELOPER"))
                return "LSImmersiveDeveloper observer";
            return "existing gameplay log boundary";
        }

        private static string InferOwner(string category)
        {
            string value = category ?? string.Empty;
            if (value.IndexOf("NPC", StringComparison.OrdinalIgnoreCase) >= 0)
                return "NPCResponse";
            if (value.IndexOf("BACKUP", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Backup";
            if (value.IndexOf("CONVOY", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("CUSTODY", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Convoy";
            if (value.IndexOf("DISPATCH", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Dispatch";
            if (value.IndexOf("CRIME_ACTIVITY", StringComparison.OrdinalIgnoreCase) >= 0)
                return "CrimeActivity";
            if (value.IndexOf("AUTHORITY", StringComparison.OrdinalIgnoreCase) >= 0)
                return "PoliceAuthority";
            if (value.IndexOf("RESPONSE", StringComparison.OrdinalIgnoreCase) >= 0)
                return "PoliceResponse";
            if (value.IndexOf("DEVELOPER", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Developer";
            return "Framework";
        }

        private static string InferKind(string category, string message, string level)
        {
            string value = (category ?? string.Empty).ToUpperInvariant();
            if (value.Contains("TASK_OWNERSHIP_CONFLICT")) return "TaskConflict";
            if (value.Contains("PHYSICAL_MISMATCH")) return "PhysicalMismatch";
            if (value.Contains("FAIL") || value.Contains("ERROR") || value.Contains("EXCEPTION")
                || value.Contains("INVALID") || value.Contains("UNAVAILABLE") || value.Contains("TIMEOUT")
                || value.Contains("REJECTED") || value.Contains("LOST")) return "Failure";
            if (value.Contains("STATE") || value.Contains("PHASE")) return "StateTransition";
            if (value.Contains("OWNERSHIP") || value.Contains("OWNER")) return "Ownership";
            if (value.Contains("TASK") || value.Contains("ESCORT") || value.Contains("FLEE")) return "TaskIssued";
            if (value.Contains("TARGET") || value.Contains("INTERCEPTION")) return "TargetObservation";
            if ((level ?? string.Empty).IndexOf("DEBUG", StringComparison.OrdinalIgnoreCase) >= 0
                && value.Contains("ANOMALY")) return "Failure";
            return "RuntimeEvent";
        }

        private static string InferState(string category, string message)
        {
            string value = (category ?? string.Empty).ToUpperInvariant();
            if (value.Contains("FLEE")) return "FLEEING";
            if (value.Contains("COMPLIANT") || value.Contains("COMPLIANCE")) return "COMPLIANT";
            if (value.Contains("RESIST")) return "RESISTING";
            if (value.Contains("ARREST") || value.Contains("HANDCUFF") || value.Contains("SECURED")) return "ARRESTED_OR_SECURING";
            if (value.Contains("ESCORT")) return "ESCORTING";
            if (value.Contains("TRANSPORT")) return "TRANSPORTING";
            if (value.Contains("ARRIVED")) return "ARRIVED";
            if (value.Contains("CLEANUP")) return "CLEANUP";
            return string.Empty;
        }

        private static Dictionary<string, string> ParseFields(string message)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string normalized = (message ?? string.Empty).Replace(" | ", ";");
            foreach (string raw in normalized.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = raw.IndexOf('=');
                if (separator <= 0)
                    continue;
                string key = raw.Substring(0, separator).Trim();
                string value = raw.Substring(separator + 1).Trim();
                if (!result.ContainsKey(key))
                    result[key] = value;
            }
            return result;
        }

        private static string Value(Dictionary<string, string> fields, params string[] names)
        {
            foreach (string name in names)
            {
                string value;
                if (fields.TryGetValue(name, out value))
                    return value;
            }
            return string.Empty;
        }

        private static int Handle(Dictionary<string, string> fields, params string[] names)
        {
            int value;
            return int.TryParse(Value(fields, names), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value : 0;
        }

        private static long Long(Dictionary<string, string> fields, string name)
        {
            long value;
            return long.TryParse(Value(fields, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value : 0L;
        }

        private static float Number(Dictionary<string, string> fields, params string[] names)
        {
            float value;
            return float.TryParse(Value(fields, names), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value : float.NaN;
        }

        private static bool Boolean(Dictionary<string, string> fields, string name)
        {
            bool value;
            return bool.TryParse(Value(fields, name), out value) && value;
        }

        private static bool TryParsePosition(string value, out Vector3 position)
        {
            position = Vector3.Zero;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            float x;
            float y;
            float z;
            if (!TryParseAxis(value, 'X', out x)
                || !TryParseAxis(value, 'Y', out y)
                || !TryParseAxis(value, 'Z', out z))
                return false;

            position = new Vector3(x, y, z);
            return true;
        }

        private static bool TryParseAxis(string value, char axis, out float result)
        {
            result = 0f;
            string prefixColon = axis + ":";
            string prefixEquals = axis + "=";
            int start = value.IndexOf(prefixColon, StringComparison.OrdinalIgnoreCase);
            int prefixLength = prefixColon.Length;
            if (start < 0)
            {
                start = value.IndexOf(prefixEquals, StringComparison.OrdinalIgnoreCase);
                prefixLength = prefixEquals.Length;
            }
            if (start < 0)
                return false;

            start += prefixLength;
            int end = value.IndexOf(' ', start);
            string number = end < 0
                ? value.Substring(start)
                : value.Substring(start, end - start);
            return float.TryParse(
                number.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out result);
        }

        private static string Sanitize(string value)
        {
            return (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace(";", ",");
        }
    }

    internal sealed class LSDeveloperPhysicalObservation
    {
        internal string PhysicalBehavior { get; set; }
        internal string TaskStatus { get; set; }
        internal int CurrentWeaponHash { get; set; }
        internal bool IsShooting { get; set; }
        internal bool IsInCombat { get; set; }
        internal bool IsAimingFromCover { get; set; }
        internal bool IsRunningArrestTask { get; set; }
        internal int CombatTargetHandle { get; set; }
    }

    internal sealed class LSDeveloperTestSession
    {
        internal string TestId { get; set; }
        internal string RuntimeSessionId { get; set; }
        internal LSDeveloperBranchDefinition Branch { get; set; }
        internal DateTime StartedAtUtc { get; set; }
        internal DateTime EndedAtUtc { get; set; }
        internal string EndReason { get; set; }
        internal bool IsEnded { get; set; }
        internal long LastFlushedSequence { get; set; }
        internal long FirstAnomalySequence { get; set; }
        internal DateTime FirstAnomalyUtc { get; set; }
        internal List<LSDeveloperCheckpoint> Checkpoints { get; private set; } = new List<LSDeveloperCheckpoint>();
        internal HashSet<int> RelevantActors { get; private set; } = new HashSet<int>();
        internal Queue<LSDeveloperTraceEvent> RecentEvents { get; private set; } = new Queue<LSDeveloperTraceEvent>();
        internal Dictionary<int, LSDeveloperTraceEvent> LastSamples { get; private set; } = new Dictionary<int, LSDeveloperTraceEvent>();
        internal HashSet<string> ReportedPhysicalMismatches { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<int, LSDeveloperBehaviorObservation> BehaviorSince { get; private set; } = new Dictionary<int, LSDeveloperBehaviorObservation>();
        internal HashSet<string> ReportedBehaviorStalls { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class LSDeveloperCheckpoint
    {
        internal string Name { get; set; }
        internal DateTime TimestampUtc { get; set; }
    }

    internal sealed class LSDeveloperTaskObservation
    {
        internal DateTime TimestampUtc { get; set; }
        internal string Owner { get; set; }
        internal string Task { get; set; }
    }

    internal sealed class LSDeveloperBehaviorObservation
    {
        internal string Key { get; set; }
        internal DateTime SinceUtc { get; set; }
    }

    internal sealed class LSDeveloperExpectedStep
    {
        internal string Name { get; private set; }
        private readonly Func<LSDeveloperTraceEvent, bool> _match;

        internal LSDeveloperExpectedStep(string name, Func<LSDeveloperTraceEvent, bool> match)
        {
            Name = name;
            _match = match;
        }

        internal bool Matches(LSDeveloperTraceEvent trace)
        {
            return _match != null && _match(trace);
        }
    }

    internal sealed class LSDeveloperBranchDefinition
    {
        internal string Id { get; private set; }
        internal string Name { get; private set; }
        internal string ExpectedBehavior { get; private set; }
        internal List<LSDeveloperExpectedStep> Steps { get; private set; }

        internal LSDeveloperBranchDefinition(
            string id,
            string name,
            string expectedBehavior,
            params LSDeveloperExpectedStep[] steps)
        {
            Id = id;
            Name = name;
            ExpectedBehavior = expectedBehavior;
            Steps = new List<LSDeveloperExpectedStep>(steps ?? new LSDeveloperExpectedStep[0]);
        }
    }

    internal static class LSDeveloperBranchCatalog
    {
        private static readonly List<LSDeveloperBranchDefinition> Branches = Create();

        internal static string[] Names()
        {
            return Branches.Select(item => item.Name).ToArray();
        }

        internal static LSDeveloperBranchDefinition Find(string name)
        {
            return Branches.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static List<LSDeveloperBranchDefinition> Create()
        {
            Func<LSDeveloperTraceEvent, bool> category = delegate(LSDeveloperTraceEvent trace)
            {
                return trace != null;
            };

            return new List<LSDeveloperBranchDefinition>
            {
                new LSDeveloperBranchDefinition(
                    "foot-citizen-document-rejection",
                    "Foot citizen document rejection",
                    "NPC document check -> foot rejection decision -> compliance, resistance, flee, or physical arrest outcome.",
                    Step("Document check", "NPC_DOCUMENT_CHECK_COMPLETED", "NPC_FOOT_DOCUMENTS_READY"),
                    Step("Foot rejection decision", "NPC_FOOT_NEGATIVE_DECISION"),
                    Step("Physical foot outcome", "NPC_FOOT_COMPLIANCE_STARTED", "NPC_FOOT_RESISTANCE_STARTED", "NPC_FOOT_FLEE_ATTEMPT", "NPC_FOOT_PLAYER_ARREST_STARTED")),
                new LSDeveloperBranchDefinition(
                    "vehicle-citizen-document-rejection",
                    "Vehicle citizen document rejection",
                    "Traffic document check -> vehicle rejection decision -> pull-away, resistance, flee, or detention outcome.",
                    Step("Traffic document check", "NPC_DOCUMENT_CHECK_COMPLETED", "NPC_TRAFFIC_DOCUMENTS_READY"),
                    Step("Traffic rejection decision", "NPC_TRAFFIC_NEGATIVE_DECISION"),
                    Step("Physical vehicle outcome", "NPC_TRAFFIC_FLEE_ATTEMPT", "NPC_TRAFFIC_RESISTANCE_STARTED", "NPC_TRAFFIC_DETENTION_READY", "NPC_TRAFFIC_PULL_OVER")),
                new LSDeveloperBranchDefinition(
                    "backup-request",
                    "Backup request",
                    "Backup request -> asset preparation -> staging/en route -> arrival -> support or handoff.",
                    Step("Backup requested", "NPC_BACKUP_HANDOFF_REQUESTED", "POLICE_BACKUP_NPC_REQUESTED", "POLICE_BACKUP_REQUESTED"),
                    Step("Assets prepared", "POLICE_BACKUP_ASSETS_REQUESTED", "POLICE_BACKUP_ASSETS_READY"),
                    Step("Backup travels or stages", "POLICE_BACKUP_STATE", "POLICE_BACKUP_UNIT_STAGED", "POLICE_BACKUP_ROUTE_RECOVERY"),
                    Step("Backup arrival or support", "POLICE_BACKUP_ARRIVED", "POLICE_BACKUP_INTERCEPTION_AREA_REACHED", "NPC_BACKUP_INTERVENTION_STARTED", "POLICE_BACKUP_NPC_PHYSICAL_CONTAINMENT_STARTED")),
                new LSDeveloperBranchDefinition(
                    "fleeing-citizen-interception",
                    "Fleeing citizen interception",
                    "Flee decision -> Backup request -> interception -> containment -> custody.",
                    Step("Fleeing task", "NPC_FOOT_FLEE_ATTEMPT", "NPC_TRAFFIC_FLEE_ATTEMPT", "NPC_FOOT_FLEE_MOVEMENT_CONFIRMED", "NPC_TRAFFIC_FLEE_MOVEMENT_CONFIRMED"),
                    Step("Backup request", "NPC_BACKUP_HANDOFF_REQUESTED", "POLICE_BACKUP_NPC_REQUESTED", "POLICE_BACKUP_INTERCEPTION_STARTED"),
                    Step("Interception boundary", "POLICE_BACKUP_INTERCEPTION_AREA_REACHED", "POLICE_BACKUP_NPC_PHYSICAL_CONTAINMENT_STARTED", "POLICE_BACKUP_NPC_BLOCKED"),
                    Step("Containment", "NPC_BACKUP_FLEEING_CONTAINMENT_CONFIRMED", "NPC_BACKUP_TRAFFIC_FLEEING_CONTAINMENT_CONFIRMED", "POLICE_BACKUP_NPC_CONTAINED", "POLICE_BACKUP_NPC_BLOCKED"),
                    Step("Custody", "NPC_BACKUP_CUSTODY_OFFER_PRESENTED", "NPC_BACKUP_CUSTODY_OFFER_ACCEPTED", "NPC_BACKUP_SUBJECT_SECURED", "POLICE_BACKUP_SUSPECT_COMPLIANT")),
                new LSDeveloperBranchDefinition(
                    "escort-to-police-vehicle",
                    "Escort to Police vehicle",
                    "Custody accepted -> subject secured -> escort movement -> door interaction -> real vehicle entry.",
                    Step("Custody accepted", "NPC_BACKUP_CUSTODY_OFFER_ACCEPTED", "NPC_BACKUP_PLAYER_CUSTODY_CONFIRMED"),
                    Step("Subject secured", "NPC_BACKUP_SUBJECT_SECURED", "NPC_FOOT_PLAYER_ARREST_COMPLETED"),
                    Step("Escort movement", "NPC_BACKUP_ESCORT_MOVEMENT_STARTED", "NPC_BACKUP_ESCORT_TO_VEHICLE_STARTED"),
                    Step("Vehicle door interaction", "NPC_BACKUP_ESCORT_REACHED_VEHICLE", "NPC_BACKUP_PRISONER_DOOR_OPENED", "POLICE_CONVOY_PRISONER_DOOR_OPENED"),
                    Step("Real vehicle entry", "NPC_BACKUP_PRISONER_ENTRY_TASK_ISSUED", "NPC_BACKUP_TRANSPORT_STARTED", "POLICE_CONVOY_PRISONER_HANDOFF_STARTED"))
            };
        }

        private static LSDeveloperExpectedStep Step(string name, params string[] categories)
        {
            return new LSDeveloperExpectedStep(
                name,
                delegate(LSDeveloperTraceEvent trace)
                {
                    if (trace == null)
                        return false;
                    return categories.Any(category => string.Equals(trace.Category, category, StringComparison.OrdinalIgnoreCase));
                });
        }
    }

    internal sealed class LSDeveloperAnalysisResult
    {
        internal string TestId { get; set; }
        internal string Branch { get; set; }
        internal string ExpectedBehavior { get; set; }
        internal string Classification { get; set; }
        internal string FirstDivergence { get; set; }
        internal string FirstProblemStep { get; set; }
        internal string Summary { get; set; }
        internal List<LSDeveloperTraceEvent> Events { get; set; } = new List<LSDeveloperTraceEvent>();
        internal List<string> MatchedSteps { get; private set; } = new List<string>();
        internal List<string> ConfirmedFacts { get; private set; } = new List<string>();
        internal List<string> ObservedState { get; private set; } = new List<string>();
        internal List<string> EventHistory { get; set; } = new List<string>();
        internal List<string> Inferences { get; private set; } = new List<string>();
        internal List<string> Unknown { get; private set; } = new List<string>();

        internal string ToNotification()
        {
            return "FIRST DIVERGENCE: " + Limit(FirstDivergence, 570)
                + "; Classification=" + Classification
                + "; Evidence in LSDeveloperTrace.log.";
        }

        private static string Limit(string value, int length)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= length)
                return value ?? string.Empty;
            return value.Substring(0, length) + "...";
        }
    }
}
