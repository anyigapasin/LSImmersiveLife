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
        private const int WaypointTeleportFadeOutMilliseconds = 300;
        private const int WaypointTeleportFadeInMilliseconds = 350;
        private const int WaypointTeleportSceneRadius = 80;
        private const int WaypointTeleportTimeoutSeconds = 15;
        private const int WaypointTeleportPollMilliseconds = 100;
        private const int WaypointTeleportSurfaceProbeRetryMilliseconds = 350;
        private const int WaypointTeleportSurfaceProbeMaximumAttempts = 4;
        private const int WaypointTeleportFootSurfaceProbeMaximumAttempts = 9;
        private const int WaypointTeleportFootGroundMaximumAttempts = 4;
        private const int WaypointTeleportFootGroundRetryMilliseconds = 400;
        private const float WaypointTeleportMaximumOffsetMeters = 250f;
        private const float WaypointTeleportDestinationStreamThresholdMeters = 50f;
        private const float WaypointTeleportMinimumFootSurfaceNormalZ = 0.65f;
        private const float WaypointTeleportMinimumVehicleSurfaceNormalZ = 0.75f;
        private const int NotificationLineCharacterLimit = 48;
        private const int NotificationLineLimit = 4;

        private enum WaypointTeleportStage
        {
            None,
            WaitingForScene,
            VerifyingDestination
        }

        private readonly object _sync = new object();
        private readonly LSIMMERSIVEPATH _paths;
        private readonly LSImmersiveLog _log;
        private readonly Func<string> _runtimeResetCommand;
        private readonly LSDeveloperSettings _settings;
        private readonly LSDeveloperBranchCatalog _branchCatalog;
        private readonly string _configurationPath;
        private readonly Queue<LSDeveloperTraceEvent> _traceBuffer =
            new Queue<LSDeveloperTraceEvent>();
        private readonly Dictionary<string, string> _lastStates =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, LSDeveloperTaskObservation> _lastTasks =
            new Dictionary<int, LSDeveloperTaskObservation>();
        private readonly Queue<LSDeveloperDispatchSceneUpdate> _dispatchSceneUpdates =
            new Queue<LSDeveloperDispatchSceneUpdate>();
        private readonly List<Blip> _dispatchInspectionBlips = new List<Blip>();
        private readonly Dictionary<int, Blip> _dispatchInspectionPedBlips =
            new Dictionary<int, Blip>();
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
        private WaypointTeleportStage _waypointTeleportStage;
        private Entity _waypointTeleportEntity;
        private Ped _waypointTeleportPlayer;
        private Vector3 _waypointTeleportWaypoint;
        private float _waypointTeleportHeightHint;
        private Vector3 _waypointTeleportOrigin;
        private float _waypointTeleportOriginHeading;
        private Vector3 _waypointTeleportDestination;
        private float _waypointTeleportDestinationHeading;
        private float _waypointTeleportOffsetMeters;
        private float _waypointTeleportSurfaceNormalZ;
        private string _waypointTeleportPlacement;
        private DateTime _waypointTeleportNextPollUtc = DateTime.MinValue;
        private DateTime _waypointSurfaceProbeNextStartUtc = DateTime.MinValue;
        private DateTime _waypointTeleportDeadlineUtc = DateTime.MinValue;
        private DateTime _waypointTeleportLandingDeadlineUtc = DateTime.MinValue;
        private int _waypointSurfaceProbeHandle;
        private int _waypointSurfaceProbeAttempts;
        private Vector3 _waypointSurfaceProbeOrigin;
        private Vector3 _waypointSurfaceProbeEnd;
        private int _waypointFootGroundAttempts;
        private DateTime _waypointFootGroundNextAttemptUtc = DateTime.MinValue;
        private string _waypointSurfaceProbeFailure = string.Empty;
        private readonly List<int> _waypointTeleportPassengerHandles = new List<int>();
        private bool _waypointTeleportIsVehicle;
        private bool _waypointTeleportSceneLoadRequested;
        private bool _waypointTeleportDestinationResolved;
        private bool _waypointTeleportMoved;
        private bool _waypointSurfaceProbeCompleted;
        private NativeListItem<string> _testEventItem;
        private NativeListItem<string> _branchTestItem;
        private NativeItem _testEventPreviewItem;
        private NativeItem _testEventPreviewValueItem;
        private NativeItem _branchPreviewItem;
        private NativeItem _branchPreviewValueItem;
        private NativeItem _checkDispatchScenesItem;
        private NativeItem _dispatchSceneInspectionStatusItem;
        private string _dispatchInspectionSearchId = string.Empty;
        private string _dispatchInspectionOccurrenceId = string.Empty;
        private string _lastInspectionFailureSignature = string.Empty;
        private DateTime _lastInspectionFailureNoticeUtc = DateTime.MinValue;
        private NativeItem _liveBranchStatusItem;
        private NativeItem _startBranchTestItem;
        private NativeItem _captureBranchTestItem;
        private NativeItem _showCheckpointTestItem;
        private NativeItem _endCheckpointTaskItem;
        private NativeMenu _testResultMenu;
        private NativeMenu _testTimelineMenu;
        private NativeMenu _testUnknownMenu;
        private NativeItem _testResultSummaryItem;
        private readonly List<NativeItem> _testResultFields = new List<NativeItem>();

        internal NativeMenu Menu { get; private set; }

        internal bool IsDispatchSceneInspectionEnabled
        {
            get
            {
                return !_shutDown && _settings.Enabled
                    && _settings.DeveloperMenuEnabled
                    && _settings.CheckDispatchScenes;
            }
        }

        internal IEnumerable<NativeMenu> Menus
        {
            get
            {
                yield return Menu;
                yield return _testResultMenu;
                yield return _testTimelineMenu;
                yield return _testUnknownMenu;
            }
        }

        internal bool IsAnyMenuVisible
        {
            get { return Menu.Visible
                || _testResultMenu.Visible
                || _testTimelineMenu.Visible
                || _testUnknownMenu.Visible; }
        }

        internal LSImmersiveDeveloper(
            LSIMMERSIVEPATH paths,
            LSImmersiveLog log,
            Func<string> runtimeResetCommand)
        {
            _paths = paths ?? throw new ArgumentNullException("paths");
            _log = log;
            _runtimeResetCommand = runtimeResetCommand;
            _configurationPath = _paths.DeveloperXmlPath;
            _settings = LSDeveloperSettings.Load(
                _configurationPath,
                delegate(string message)
                {
                    if (_log != null)
                        _log.Debug("DEVELOPER_CONFIG", message);
                });
            _branchCatalog = LSDeveloperBranchCatalog.Load(
                _paths.DeveloperBranchesXmlPath,
                delegate(string message)
                {
                    if (_log != null)
                        _log.Debug("DEVELOPER_BRANCH_CATALOG", message);
                });

            Menu = LSImmersiveMenuFactory.Create(
                "Developer",
                "Inspect the Development Session");
            _testResultMenu = LSImmersiveMenuFactory.Create(
                "Developer Test Result",
                "Review the captured test evidence");
            _testTimelineMenu = LSImmersiveMenuFactory.Create(
                "Evidence Timeline",
                "First divergence and recent events");
            _testUnknownMenu = LSImmersiveMenuFactory.Create(
                "What Remains Unknown",
                "Evidence not exposed by the runtime");
            Menu.NoItemsText = "Developer Mode is disabled in LSDeveloper.xml.";
            _testResultMenu.NoItemsText = "Analyze a completed or active test to populate this report.";
            _testTimelineMenu.NoItemsText = "No test evidence has been analyzed.";
            _testUnknownMenu.NoItemsText = "No unknown evidence has been analyzed.";
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
            if (_shutDown)
                return;

            ProcessWaypointTeleport();

            if (!_settings.Enabled)
                return;

            ProcessDispatchSceneInspection();

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
                CancelWaypointTeleport("Script shutdown", false);

                if (_activeTest != null)
                    EndTestInternal("Script shutdown", false);

                ClearDispatchSceneInspectionBlips();

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
            AddHeader("Developer Tools");

            _checkDispatchScenesItem = new NativeItem(
                "Check Dispatch Scenes: OFF",
                "Inspect the live Dispatch preparation. Prepared participants stay hidden until the offer is published.");
            _checkDispatchScenesItem.Activated += delegate
            {
                ToggleDispatchSceneInspection();
            };
            Menu.Add(_checkDispatchScenesItem);

            _dispatchSceneInspectionStatusItem = new NativeItem(
                "Dispatch Scene Check",
                "Inspection is disabled.");
            _dispatchSceneInspectionStatusItem.Enabled = false;
            Menu.Add(_dispatchSceneInspectionStatusItem);

            AddAction(
                "Teleport to Waypoint",
                "Teleport to terrain, hills, beaches, remote places, or rooftops at the pin. Vehicle and occupants stay together; safe fallback is within 250 m.",
                BeginWaypointTeleport);

            AddAction(
                "Reset Runtime",
                "Recover Police runtime tracking. Scene actors use their safe cleanup lifecycle; your Player ped and Police profile stay unchanged.",
                ResetRuntime);

            AddHeader("Branch Tests");

            string[] testEvents = _branchCatalog.EventNames();
            string[] eventChoices = testEvents.Length == 0
                ? new[] { "No documented test events" }
                : testEvents;

            _testEventItem = new NativeListItem<string>(
                "Test Event",
                "Select a gameplay event.",
                eventChoices);
            _testEventItem.SelectedItem = eventChoices[0];
            _testEventItem.Enabled = testEvents.Length > 0;
            _testEventItem.ItemChanged += delegate(object sender, ItemChangedEventArgs<string> args)
            {
                UpdateBranchTestChoices(args.Object);
            };
            Menu.Add(_testEventItem);

            _testEventPreviewItem = new NativeItem(
                "Selected Test Event",
                "The selected gameplay event.");
            _testEventPreviewItem.Enabled = false;
            Menu.Add(_testEventPreviewItem);

            _testEventPreviewValueItem = new NativeItem("No test event selected.");
            _testEventPreviewValueItem.Enabled = false;
            Menu.Add(_testEventPreviewValueItem);

            _branchTestItem = new NativeListItem<string>(
                "Select Branch",
                "Choose a branch. Its full name appears below.",
                new[] { "No documented branches" });
            _branchTestItem.SelectedItem = "No documented branches";
            _branchTestItem.Enabled = false;
            _branchTestItem.ItemChanged += delegate
            {
                UpdateBranchPreview();
                RefreshMenuControls();
            };
            Menu.Add(_branchTestItem);

            _branchPreviewItem = new NativeItem(
                "Selected Branch",
                "The full selected branch name.");
            _branchPreviewItem.Enabled = false;
            Menu.Add(_branchPreviewItem);

            _branchPreviewValueItem = new NativeItem("No documented branch selected.");
            _branchPreviewValueItem.Enabled = false;
            Menu.Add(_branchPreviewValueItem);

            _startBranchTestItem = AddAction(
                "Start the Branch Test",
                "Begin a bounded observation of the selected documented branch.",
                StartSelectedBranchTest);

            _liveBranchStatusItem = new NativeItem("Live Branch Test Not Started");
            _liveBranchStatusItem.Enabled = false;
            Menu.Add(_liveBranchStatusItem);

            _captureBranchTestItem = AddAction(
                "Capture and Conclude the Test",
                "Capture a final flow checkpoint and end the active test.",
                CaptureCheckpoint);
            _showCheckpointTestItem = AddAction(
                "Show Checkpoint Test",
                "Open the readable result for the latest captured or concluded test.",
                AnalyzeCurrentTest);
            _endCheckpointTaskItem = AddAction(
                "End the Checkpoint Task",
                "End the active Developer test without recording a final checkpoint.",
                delegate { EndTestInternal("Player ended the test without a final checkpoint", true); });

            _testResultSummaryItem = new NativeItem(
                "Test Result: Not analyzed",
                "Select Show Checkpoint Test to display the latest readable report.");
            _testResultSummaryItem.Enabled = false;
            _testResultMenu.Add(_testResultSummaryItem);

            string[] reportFields =
            {
                "Test Event / Branch",
                "Test ID",
                "Runtime Session ID",
                "Expected Event",
                "Flow Connection",
                "Observed Event",
                "Actor",
                "Owner System File",
                "Target",
                "State",
                "Task",
                "Physical Behavior",
                "First Divergence",
                "Source",
                "Result"
            };
            foreach (string field in reportFields)
            {
                NativeItem row = new NativeItem(field, "No test result has been analyzed.");
                row.Enabled = false;
                _testResultFields.Add(row);
                _testResultMenu.Add(row);
            }

            _testResultMenu.AddSubMenu(_testTimelineMenu);
            _testResultMenu.AddSubMenu(_testUnknownMenu);
            Menu.AddSubMenu(_testResultMenu);
            _testTimelineMenu.Parent = _testResultMenu;
            _testUnknownMenu.Parent = _testResultMenu;
            _testResultMenu.Parent = Menu;

            if (testEvents.Length > 0)
                UpdateBranchTestChoices(eventChoices[0]);
            else
            {
                UpdateTestEventPreview(string.Empty);
                UpdateBranchPreview();
                RefreshMenuControls();
            }
            RefreshDispatchSceneInspectionMenu();
        }

        private void UpdateBranchTestChoices(string eventName)
        {
            string[] branchChoices = _branchCatalog.BranchNames(eventName);
            _branchTestItem.Clear();

            if (branchChoices.Length == 0)
            {
                _branchTestItem.Add("No documented branches");
                _branchTestItem.SelectedItem = "No documented branches";
                _branchTestItem.Enabled = false;
            }
            else
            {
                for (int i = 0; i < branchChoices.Length; i++)
                    _branchTestItem.Add("Branch "
                        + (i + 1).ToString(CultureInfo.InvariantCulture)
                        + " of "
                        + branchChoices.Length.ToString(CultureInfo.InvariantCulture));
                _branchTestItem.SelectedIndex = 0;
                _branchTestItem.Enabled = true;
            }

            UpdateTestEventPreview(eventName);
            UpdateBranchPreview();
            RefreshMenuControls();
        }

        private void RefreshMenuControls()
        {
            if (_testEventItem == null || _branchTestItem == null)
                return;

            bool developerEnabled = _settings.Enabled && _settings.DeveloperMenuEnabled;
            bool hasSelectedBranch = _branchCatalog.Find(
                _testEventItem.SelectedItem,
                SelectedBranchName()) != null;
            bool hasTest = _activeTest != null || _lastTest != null;

            if (_startBranchTestItem != null)
                _startBranchTestItem.Enabled = developerEnabled && hasSelectedBranch && _activeTest == null;
            if (_captureBranchTestItem != null)
                _captureBranchTestItem.Enabled = developerEnabled && _activeTest != null;
            if (_showCheckpointTestItem != null)
                _showCheckpointTestItem.Enabled = developerEnabled && hasTest;
            if (_endCheckpointTaskItem != null)
                _endCheckpointTaskItem.Enabled = developerEnabled && _activeTest != null;
            if (_checkDispatchScenesItem != null)
                _checkDispatchScenesItem.Enabled = developerEnabled;

            if (_liveBranchStatusItem != null)
            {
                _liveBranchStatusItem.Title = _activeTest == null
                    ? "Live Branch Test Not Started"
                    : "Live Branch Test Started";
                _liveBranchStatusItem.Description = _activeTest == null
                    ? "No branch test is currently active."
                    : _activeTest.Branch.EventName + " — " + _activeTest.Branch.Name;
            }
            RefreshDispatchSceneInspectionMenu();
        }

        private void UpdateTestEventPreview(string eventName)
        {
            if (_testEventPreviewValueItem == null)
                return;
            _testEventPreviewValueItem.Title = string.IsNullOrWhiteSpace(eventName)
                ? "No Test Event selected."
                : eventName;
        }

        private void UpdateBranchPreview()
        {
            if (_branchPreviewValueItem == null)
                return;
            LSDeveloperBranchDefinition branch = _branchCatalog.Find(
                _testEventItem == null ? string.Empty : _testEventItem.SelectedItem,
                SelectedBranchName());
            _branchPreviewValueItem.Title = branch == null
                ? "No documented branch selected."
                : branch.Name;
            _branchPreviewValueItem.Description = branch == null
                ? "Choose a documented branch above."
                : Limit(branch.ExpectedBehavior ?? branch.Name, 220);
        }

        private string SelectedBranchName()
        {
            if (_testEventItem == null || _branchTestItem == null)
                return string.Empty;

            string[] branchNames = _branchCatalog.BranchNames(_testEventItem.SelectedItem);
            int selectedIndex = _branchTestItem.SelectedIndex;
            return selectedIndex < 0 || selectedIndex >= branchNames.Length
                ? string.Empty
                : branchNames[selectedIndex];
        }

        private void BeginWaypointTeleport()
        {
            if (_waypointTeleportStage != WaypointTeleportStage.None)
            {
                Notify("A waypoint move is already in progress.");
                return;
            }

            if (!_settings.Enabled || !_settings.DeveloperMenuEnabled)
            {
                Notify("Developer Mode is disabled in LSDeveloper.xml.");
                return;
            }

            if (!Game.IsWaypointActive)
            {
                Notify("Set a map waypoint before choosing Teleport to Waypoint.");
                return;
            }

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
            {
                Notify("The Player is not available for the waypoint move.");
                return;
            }

            if (Function.Call<bool>(Hash.IS_SCREEN_FADED_OUT)
                || Function.Call<bool>(Hash.IS_SCREEN_FADING_OUT)
                || Function.Call<bool>(Hash.IS_SCREEN_FADING_IN))
            {
                Notify("Wait for the current screen transition to finish before teleporting.");
                return;
            }

            Vector3 waypoint;
            Entity target = player;
            bool moveVehicle = false;
            List<int> passengerHandles = new List<int>();
            Vector3 origin = Vector3.Zero;
            float originHeading = 0f;
            try
            {
                waypoint = World.WaypointPosition;
                moveVehicle = player.IsInVehicle();
                if (moveVehicle)
                {
                    Vehicle vehicle = player.CurrentVehicle;
                    if (vehicle == null || !vehicle.Exists())
                    {
                        Notify("The Player's current vehicle is unavailable; nothing was moved.");
                        return;
                    }
                    target = vehicle;
                    Ped[] occupants = vehicle.Occupants;
                    if (occupants != null)
                    {
                        foreach (Ped occupant in occupants)
                        {
                            if (occupant != null && occupant.Exists()
                                && occupant.Handle != player.Handle)
                                passengerHandles.Add(occupant.Handle);
                        }
                    }
                }
                origin = target.Position;
                originHeading = target.Heading;
            }
            catch (Exception error)
            {
                LogException("DEVELOPER_WAYPOINT_TELEPORT_PREPARE_FAILED", error);
                Notify("The waypoint or current vehicle could not be read.");
                return;
            }

            if (float.IsNaN(waypoint.X) || float.IsInfinity(waypoint.X)
                || float.IsNaN(waypoint.Y) || float.IsInfinity(waypoint.Y)
                || float.IsNaN(waypoint.Z) || float.IsInfinity(waypoint.Z))
            {
                Notify("The map waypoint does not have a usable position.");
                return;
            }

            _waypointTeleportPlayer = player;
            _waypointTeleportEntity = target;
            _waypointTeleportWaypoint = waypoint;
            _waypointTeleportHeightHint = waypoint.Z;
            if (Math.Abs(waypoint.Z) < 0.1f)
            {
                try
                {
                    float approximateHeight = World.GetApproxHeightForPoint(
                        new Vector2(waypoint.X, waypoint.Y));
                    _waypointTeleportHeightHint = !float.IsNaN(approximateHeight)
                        && !float.IsInfinity(approximateHeight)
                        && approximateHeight > -50f && approximateHeight < 1800f
                        ? approximateHeight : 50f;
                }
                catch { _waypointTeleportHeightHint = 50f; }
            }
            _waypointTeleportOrigin = origin;
            _waypointTeleportOriginHeading = originHeading;
            _waypointTeleportDestination = Vector3.Zero;
            _waypointTeleportDestinationHeading = 0f;
            _waypointTeleportOffsetMeters = 0f;
            _waypointTeleportPlacement = string.Empty;
            _waypointTeleportSurfaceNormalZ = 0f;
            _waypointTeleportIsVehicle = moveVehicle;
            _waypointTeleportPassengerHandles.Clear();
            _waypointTeleportPassengerHandles.AddRange(passengerHandles);
            _waypointTeleportDestinationResolved = false;
            _waypointTeleportMoved = false;
            _waypointSurfaceProbeHandle = 0;
            _waypointSurfaceProbeAttempts = 0;
            _waypointSurfaceProbeOrigin = Vector3.Zero;
            _waypointSurfaceProbeEnd = Vector3.Zero;
            _waypointFootGroundAttempts = 0;
            _waypointFootGroundNextAttemptUtc = DateTime.MinValue;
            _waypointSurfaceProbeFailure = string.Empty;
            _waypointSurfaceProbeNextStartUtc = DateTime.MinValue;
            _waypointSurfaceProbeCompleted = false;
            _waypointTeleportNextPollUtc = DateTime.MinValue;
            _waypointTeleportDeadlineUtc = DateTime.UtcNow.AddSeconds(
                WaypointTeleportTimeoutSeconds);
            _waypointTeleportLandingDeadlineUtc = DateTime.MinValue;
            _waypointTeleportStage = WaypointTeleportStage.WaitingForScene;

            try
            {
                Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                    waypoint.X, waypoint.Y, _waypointTeleportHeightHint);
                _waypointTeleportSceneLoadRequested = Function.Call<bool>(
                    Hash.NEW_LOAD_SCENE_START_SPHERE,
                    waypoint.X,
                    waypoint.Y,
                    _waypointTeleportHeightHint,
                    (float)WaypointTeleportSceneRadius,
                    0);
                if (!_waypointTeleportSceneLoadRequested)
                {
                    CancelWaypointTeleport(
                        "GTA did not start streaming the waypoint area. Nothing was moved.",
                        true);
                    return;
                }
                Function.Call(Hash.DO_SCREEN_FADE_OUT,
                    WaypointTeleportFadeOutMilliseconds);

                LogRuntime(
                    "DEVELOPER_WAYPOINT_TELEPORT_STARTED",
                    "Player=" + player.Handle
                    + "; Vehicle=" + (moveVehicle ? target.Handle.ToString(CultureInfo.InvariantCulture) : "none")
                    + "; PassengerHandles=" + string.Join(",", passengerHandles)
                    + "; Waypoint=" + waypoint
                    + "; StreamHeightHint=" + _waypointTeleportHeightHint.ToString("0.0", CultureInfo.InvariantCulture));
                Notify("Streaming the waypoint area and checking for a safe surface.");
            }
            catch (Exception error)
            {
                LogException("DEVELOPER_WAYPOINT_TELEPORT_START_FAILED", error);
                CancelWaypointTeleport("The waypoint area could not be loaded.", true);
            }
        }

        private void ResetRuntime()
        {
            if (_runtimeResetCommand == null)
            {
                Notify("~b~RUNTIME RESET~s~~n~No runtime reset route is available.");
                return;
            }

            try
            {
                string result = _runtimeResetCommand();
                bool recovered = !string.IsNullOrWhiteSpace(result)
                    && result.StartsWith("Police runtime recovered.",
                        StringComparison.OrdinalIgnoreCase);
                bool roleUnavailable = !string.IsNullOrWhiteSpace(result)
                    && result.StartsWith("No Police runtime is registered",
                        StringComparison.OrdinalIgnoreCase);
                bool authorityInactive = !string.IsNullOrWhiteSpace(result)
                    && result.StartsWith("Police Authority must be active",
                        StringComparison.OrdinalIgnoreCase);
                if (_log != null)
                    _log.Runtime("DEVELOPER_RUNTIME_RESET",
                        string.IsNullOrWhiteSpace(result) ? "No result returned." : result);
                Notify(recovered
                    ? "~b~RUNTIME RESET~s~~n~Systems recovered. Scene actors retained."
                        + "~n~Player and profile unchanged."
                    : roleUnavailable
                        ? "~b~RUNTIME RESET~s~~n~No Police runtime for this role."
                            + "~n~Role unchanged."
                        : authorityInactive
                            ? "~b~RUNTIME RESET~s~~n~Police Authority is inactive."
                                + "~n~No reset performed."
                            : "~b~RUNTIME RESET~s~~n~No reset performed."
                                + "~n~See LSDebug.log for details.");
            }
            catch (Exception ex)
            {
                if (_log != null)
                    _log.Exception("DEVELOPER_RUNTIME_RESET_FAILED", ex);
                Notify("~r~RUNTIME RESET FAILED~s~~n~See LSDebug.log for details.");
            }
        }

        private void ProcessWaypointTeleport()
        {
            if (_waypointTeleportStage == WaypointTeleportStage.None)
                return;

            DateTime now = DateTime.UtcNow;
            if (now < _waypointTeleportNextPollUtc)
                return;
            _waypointTeleportNextPollUtc = now.AddMilliseconds(
                WaypointTeleportPollMilliseconds);

            try
            {
                bool screenReady = Function.Call<bool>(Hash.IS_SCREEN_FADED_OUT);
                bool sceneReady = !_waypointTeleportSceneLoadRequested
                    || Function.Call<bool>(Hash.IS_NEW_LOAD_SCENE_LOADED);
                if (!screenReady || !sceneReady)
                {
                    if (now >= _waypointTeleportDeadlineUtc)
                    {
                        if (_waypointTeleportStage
                            == WaypointTeleportStage.VerifyingDestination)
                        {
                            FinishWaypointTeleport(
                                false,
                                "Moved=" + _waypointTeleportDestination
                                + "; CollisionLoaded=false; Arrival=Unconfirmed; SceneStreamingTimedOut=true",
                                "GTA could not finish loading the arrival area. The move may have happened, but landing is unconfirmed.");
                        }
                        else
                            CancelWaypointTeleport(
                                "The waypoint area did not finish loading. Nothing was moved.",
                                true);
                    }
                    return;
                }

                if (_waypointTeleportPlayer == null
                    || !_waypointTeleportPlayer.Exists()
                    || Game.Player.Character == null
                    || !Game.Player.Character.Exists()
                    || Game.Player.Character.Handle != _waypointTeleportPlayer.Handle
                    || _waypointTeleportEntity == null
                    || !_waypointTeleportEntity.Exists())
                {
                    CancelWaypointTeleport(
                        "The Player or selected vehicle changed during loading. Nothing was moved.",
                        true);
                    return;
                }

                if (_waypointTeleportIsVehicle)
                {
                    Vehicle currentVehicle = _waypointTeleportPlayer.CurrentVehicle;
                    if (!_waypointTeleportPlayer.IsInVehicle()
                        || currentVehicle == null
                        || !currentVehicle.Exists()
                        || currentVehicle.Handle != _waypointTeleportEntity.Handle)
                    {
                        CancelWaypointTeleport(
                            "The Player left the selected vehicle during loading. Nothing was moved.",
                            true);
                        return;
                    }
                }
                else if (_waypointTeleportPlayer.IsInVehicle())
                {
                    CancelWaypointTeleport(
                        "The Player entered a vehicle during loading. Nothing was moved.",
                        true);
                    return;
                }

                if (_waypointTeleportStage
                    == WaypointTeleportStage.VerifyingDestination)
                {
                    Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                        _waypointTeleportDestination.X,
                        _waypointTeleportDestination.Y,
                        _waypointTeleportDestination.Z);
                    bool collisionLoaded = Function.Call<bool>(
                        Hash.HAS_COLLISION_LOADED_AROUND_ENTITY,
                        _waypointTeleportEntity.Handle);
                    if (collisionLoaded)
                    {
                        float distanceFromDestination = SpatialDistance(
                            _waypointTeleportEntity.Position,
                            _waypointTeleportDestination);
                        if (distanceFromDestination > 10f)
                        {
                            FinishWaypointTeleport(
                                false,
                                "Moved=" + _waypointTeleportEntity.Position
                                + "; CollisionLoaded=true; Arrival=OutsideResolvedLandingArea"
                                + "; DistanceMeters="
                                + distanceFromDestination.ToString("0.0", CultureInfo.InvariantCulture),
                                "The vehicle moved outside the checked landing area. Arrival is unconfirmed; inspect the position before continuing.");
                            return;
                        }

                        if (_waypointTeleportIsVehicle)
                        {
                            Vehicle vehicle = _waypointTeleportEntity as Vehicle;
                            Ped[] occupants = vehicle == null ? null : vehicle.Occupants;
                            bool passengersRetained = _waypointTeleportPassengerHandles.Count == 0
                                || (occupants != null
                                && _waypointTeleportPassengerHandles.All(handle =>
                                    occupants.Any(occupant => occupant != null
                                        && occupant.Exists()
                                        && occupant.Handle == handle)));
                            if (!passengersRetained)
                            {
                                FinishWaypointTeleport(false,
                                    "Vehicle=" + _waypointTeleportEntity.Handle
                                    + "; Arrival=PassengerOccupancyUnconfirmed",
                                    "The vehicle arrived, but its original passenger occupancy was not confirmed.");
                                return;
                            }
                        }

                        FinishWaypointTeleport(true,
                            "Moved=" + _waypointTeleportEntity.Position
                            + "; OffsetFromWaypointMeters="
                            + _waypointTeleportOffsetMeters.ToString("0.0", CultureInfo.InvariantCulture)
                            + "; Placement=" + (_waypointTeleportPlacement ?? "Unknown")
                            + "; SurfaceNormalZ="
                            + (_waypointTeleportSurfaceNormalZ <= 0f
                                ? "not-captured"
                                : _waypointTeleportSurfaceNormalZ.ToString("0.00", CultureInfo.InvariantCulture))
                            + "; CollisionAroundEntity=true; Arrival=Confirmed"
                            + "; PassengerHandlesRetained="
                            + string.Join(",", _waypointTeleportPassengerHandles));
                        return;
                    }

                    if (now >= _waypointTeleportLandingDeadlineUtc)
                    {
                        FinishWaypointTeleport(
                            false,
                            "Moved=" + _waypointTeleportEntity.Position
                            + "; CollisionAroundEntity=false; Arrival=Unconfirmed; LandingWaitSeconds=5",
                            "GTA did not confirm collision at the arrival point. The Player or vehicle may have moved, but landing is unconfirmed.");
                        return;
                    }
                    return;
                }

                if (!_waypointSurfaceProbeCompleted
                    && !_waypointTeleportDestinationResolved)
                {
                    int maximumSurfaceAttempts = _waypointTeleportIsVehicle
                        ? WaypointTeleportSurfaceProbeMaximumAttempts
                        : WaypointTeleportFootSurfaceProbeMaximumAttempts;
                    if (_waypointSurfaceProbeHandle == 0)
                    {
                        if (now < _waypointSurfaceProbeNextStartUtc)
                            return;

                        _waypointSurfaceProbeAttempts++;
                        _waypointSurfaceProbeHandle = StartWaypointSurfaceProbe();
                        if (_waypointSurfaceProbeHandle == 0)
                        {
                            LogWaypointSurfaceProbe(0, false, Vector3.Zero,
                                Vector3.Zero, "Probe did not start.");
                            if (_waypointSurfaceProbeAttempts
                                >= maximumSurfaceAttempts)
                                _waypointSurfaceProbeCompleted = true;
                            else
                                _waypointSurfaceProbeNextStartUtc = now.AddMilliseconds(
                                    WaypointTeleportSurfaceProbeRetryMilliseconds);
                            return;
                        }

                        return;
                    }

                    bool surfaceHit;
                    Vector3 surfacePosition;
                    Vector3 surfaceNormal;
                    int surfaceStatus = ReadWaypointSurfaceProbe(
                        out surfaceHit,
                        out surfacePosition,
                        out surfaceNormal);
                    if (surfaceStatus == 1)
                    {
                        if (now >= _waypointTeleportDeadlineUtc)
                            CancelWaypointTeleport(
                                "The waypoint surface check did not finish loading. Nothing was moved.",
                                true);
                        return;
                    }

                    _waypointSurfaceProbeHandle = 0;
                    if (surfaceStatus == 2)
                    {
                        Vector3 surfaceDestination;
                        float surfaceHeading;
                        float surfaceOffsetMeters;
                        float surfaceNormalZ;
                        string surfaceFailure = string.Empty;
                        if (surfaceHit
                            && TryResolveWaypointSurface(
                                surfacePosition,
                                surfaceNormal,
                                out surfaceDestination,
                                out surfaceHeading,
                                out surfaceOffsetMeters,
                                out surfaceNormalZ,
                                out surfaceFailure))
                        {
                            _waypointTeleportDestination = surfaceDestination;
                            _waypointTeleportDestinationHeading = surfaceHeading;
                            _waypointTeleportOffsetMeters = surfaceOffsetMeters;
                            _waypointTeleportSurfaceNormalZ = surfaceNormalZ;
                            _waypointTeleportPlacement = "SolidSurface";
                            _waypointTeleportDestinationResolved = true;
                        }
                        else if (surfaceHit)
                            _waypointSurfaceProbeFailure = surfaceFailure;

                        LogWaypointSurfaceProbe(surfaceStatus, surfaceHit,
                            surfacePosition, surfaceNormal,
                            _waypointTeleportDestinationResolved
                                ? "Accepted=" + _waypointTeleportDestination
                                : surfaceHit ? surfaceFailure : "No surface hit.");

                        if (!_waypointTeleportDestinationResolved
                            && _waypointSurfaceProbeAttempts
                                < maximumSurfaceAttempts)
                        {
                            _waypointSurfaceProbeNextStartUtc = now.AddMilliseconds(
                                WaypointTeleportSurfaceProbeRetryMilliseconds);
                            return;
                        }

                        _waypointSurfaceProbeCompleted = true;
                    }
                    else if (_waypointSurfaceProbeAttempts
                        < maximumSurfaceAttempts)
                    {
                        LogWaypointSurfaceProbe(surfaceStatus, false,
                            Vector3.Zero, Vector3.Zero,
                            "Probe did not complete with a hit result.");
                        _waypointSurfaceProbeNextStartUtc = now.AddMilliseconds(
                            WaypointTeleportSurfaceProbeRetryMilliseconds);
                        return;
                    }
                    else
                    {
                        LogWaypointSurfaceProbe(surfaceStatus, false,
                            Vector3.Zero, Vector3.Zero,
                            "Probe did not complete with a hit result.");
                        _waypointSurfaceProbeCompleted = true;
                    }
                }

                if (!_waypointTeleportDestinationResolved)
                {
                    if (!_waypointTeleportIsVehicle
                        && now < _waypointFootGroundNextAttemptUtc)
                        return;
                    if (!_waypointTeleportIsVehicle)
                        _waypointFootGroundAttempts++;
                    Vector3 destination;
                    float heading;
                    float offsetMeters;
                    string placement;
                    string failureReason;
                    if (!TryResolveWaypointTeleportDestination(
                        out destination,
                        out heading,
                        out offsetMeters,
                        out placement,
                        out failureReason))
                    {
                        if (!_waypointTeleportIsVehicle
                            && _waypointFootGroundAttempts
                                < WaypointTeleportFootGroundMaximumAttempts
                            && now < _waypointTeleportDeadlineUtc)
                        {
                            _waypointFootGroundNextAttemptUtc = now.AddMilliseconds(
                                WaypointTeleportFootGroundRetryMilliseconds);
                            return;
                        }
                        CancelWaypointTeleport(failureReason
                            + " Surface probes=" + _waypointSurfaceProbeAttempts
                            + "; Ground retries=" + _waypointFootGroundAttempts
                            + "; Height hint="
                            + _waypointTeleportHeightHint.ToString("0.0", CultureInfo.InvariantCulture)
                            + (string.IsNullOrWhiteSpace(_waypointSurfaceProbeFailure)
                                ? string.Empty
                                : "; last surface=" + _waypointSurfaceProbeFailure), true);
                        return;
                    }

                    _waypointTeleportDestination = destination;
                    _waypointTeleportDestinationHeading = heading;
                    _waypointTeleportOffsetMeters = offsetMeters;
                    _waypointTeleportPlacement = placement;
                    _waypointTeleportSurfaceNormalZ = 0f;
                    _waypointTeleportDestinationResolved = true;

                    if (offsetMeters > WaypointTeleportDestinationStreamThresholdMeters)
                    {
                        if (_waypointTeleportSceneLoadRequested)
                        {
                            Function.Call(Hash.NEW_LOAD_SCENE_STOP);
                            _waypointTeleportSceneLoadRequested = false;
                        }

                        Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                            destination.X, destination.Y, destination.Z);
                        _waypointTeleportSceneLoadRequested = Function.Call<bool>(
                            Hash.NEW_LOAD_SCENE_START_SPHERE,
                            destination.X,
                            destination.Y,
                            destination.Z,
                            (float)WaypointTeleportSceneRadius,
                            0);
                        if (!_waypointTeleportSceneLoadRequested)
                        {
                            CancelWaypointTeleport(
                                "GTA did not stream the nearby fallback position. Nothing was moved.",
                                true);
                            return;
                        }

                        // A fallback point can be outside the originally streamed area.
                        // Let the existing 100 ms poll wait for that destination to load.
                        return;
                    }
                }

                Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                    _waypointTeleportDestination.X,
                    _waypointTeleportDestination.Y,
                    _waypointTeleportDestination.Z);
                _waypointTeleportMoved = true;
                _waypointTeleportEntity.Position = _waypointTeleportDestination;
                if (_waypointTeleportIsVehicle)
                {
                    _waypointTeleportEntity.Heading = _waypointTeleportDestinationHeading;
                    _waypointTeleportEntity.Velocity = Vector3.Zero;
                }
                else
                    _waypointTeleportEntity.Velocity = Vector3.Zero;

                _waypointTeleportStage = WaypointTeleportStage.VerifyingDestination;
                _waypointTeleportLandingDeadlineUtc = now.AddSeconds(5);
                _waypointTeleportDeadlineUtc = _waypointTeleportLandingDeadlineUtc;
                _waypointTeleportNextPollUtc = now.AddMilliseconds(
                    WaypointTeleportPollMilliseconds);
            }
            catch (Exception error)
            {
                LogException("DEVELOPER_WAYPOINT_TELEPORT_FAILED", error);
                CancelWaypointTeleport(
                    "The waypoint move could not be completed safely.",
                    true);
            }
        }

        private void LogWaypointSurfaceProbe(int status, bool hit,
            Vector3 position, Vector3 normal, string result)
        {
            LogRuntime("DEVELOPER_WAYPOINT_SURFACE_PROBE",
                "Attempt=" + _waypointSurfaceProbeAttempts
                + "; Origin=" + _waypointSurfaceProbeOrigin
                + "; End=" + _waypointSurfaceProbeEnd
                + "; Status=" + status
                + "; Hit=" + hit
                + "; HitPosition=" + position
                + "; Normal=" + normal
                + "; SceneLoaded=true; CollisionRequested=true"
                + "; GroundSample=not-run; SafePed=not-run"
                + "; Result=" + result);
        }

        private int StartWaypointSurfaceProbe()
        {
            Vector3 waypoint = _waypointTeleportWaypoint;
            // The extra foot probes reach nearby pavement without changing the
            // established four-attempt vehicle path.
            switch (_waypointSurfaceProbeAttempts)
            {
                case 2: waypoint.X += 8f; break;
                case 3: waypoint.X -= 8f; break;
                case 4: waypoint.Y += 8f; break;
                case 5: waypoint.Y -= 8f; break;
                case 6: waypoint.X += 16f; break;
                case 7: waypoint.X -= 16f; break;
                case 8: waypoint.X += 24f; break;
                case 9: waypoint.X -= 24f; break;
            }
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                waypoint.X, waypoint.Y, _waypointTeleportHeightHint);
            // Use a local ray for on-foot Z=0 pins. Keep the established
            // vehicle probe range so the working road fallback is unchanged.
            float startZ = _waypointTeleportIsVehicle
                ? Math.Max(1800f, waypoint.Z + 500f)
                : Math.Abs(_waypointTeleportWaypoint.Z) < 0.1f
                    ? _waypointTeleportHeightHint + 75f
                    : _waypointTeleportWaypoint.Z + 4f;
            float endZ = _waypointTeleportIsVehicle
                ? Math.Min(-250f, waypoint.Z - 100f)
                : Math.Abs(_waypointTeleportWaypoint.Z) < 0.1f
                    ? _waypointTeleportHeightHint - 100f
                    : _waypointTeleportWaypoint.Z - 20f;

            _waypointSurfaceProbeOrigin = new Vector3(waypoint.X, waypoint.Y, startZ);
            _waypointSurfaceProbeEnd = new Vector3(waypoint.X, waypoint.Y, endZ);

            return Function.Call<int>(
                Hash.START_SHAPE_TEST_LOS_PROBE,
                waypoint.X,
                waypoint.Y,
                startZ,
                waypoint.X,
                waypoint.Y,
                endZ,
                (int)(_waypointTeleportIsVehicle
                    ? IntersectFlags.Map
                    : IntersectFlags.Map | IntersectFlags.Objects),
                0,
                7);
        }

        private int ReadWaypointSurfaceProbe(
            out bool hit,
            out Vector3 hitPosition,
            out Vector3 surfaceNormal)
        {
            hit = false;
            hitPosition = Vector3.Zero;
            surfaceNormal = Vector3.Zero;

            using (OutputArgument hitOutput = new OutputArgument())
            using (OutputArgument positionOutput = new OutputArgument())
            using (OutputArgument normalOutput = new OutputArgument())
            using (OutputArgument entityOutput = new OutputArgument())
            {
                int status = Function.Call<int>(
                    Hash.GET_SHAPE_TEST_RESULT,
                    _waypointSurfaceProbeHandle,
                    hitOutput,
                    positionOutput,
                    normalOutput,
                    entityOutput);
                if (status != 2)
                    return status;

                hit = hitOutput.GetResult<bool>();
                hitPosition = positionOutput.GetResult<Vector3>();
                surfaceNormal = normalOutput.GetResult<Vector3>();
                return status;
            }
        }

        private bool TryResolveWaypointSurface(
            Vector3 hitPosition,
            Vector3 surfaceNormal,
            out Vector3 destination,
            out float heading,
            out float offsetMeters,
            out float surfaceNormalZ,
            out string failureReason)
        {
            destination = Vector3.Zero;
            heading = 0f;
            offsetMeters = 0f;
            surfaceNormalZ = 0f;
            failureReason = "The waypoint surface did not pass the safe landing checks.";

            if (!IsFinitePosition(hitPosition)
                || !IsFinitePosition(surfaceNormal))
                return false;

            float minimumNormalZ = _waypointTeleportIsVehicle
                ? WaypointTeleportMinimumVehicleSurfaceNormalZ
                : WaypointTeleportMinimumFootSurfaceNormalZ;
            if (surfaceNormal.Z < minimumNormalZ)
            {
                failureReason = "The surface at the pin is too steep for a safe landing.";
                return false;
            }

            if (!_waypointTeleportIsVehicle)
            {
                if (Math.Abs(_waypointTeleportWaypoint.Z) >= 0.1f
                    && Math.Abs(hitPosition.Z - _waypointTeleportWaypoint.Z) > 4f)
                {
                    failureReason = "The solid surface is at a different height from the selected pin.";
                    return false;
                }
                Vector3 checkedFootDestination;
                float checkedFootOffset;
                if (!TryResolveVerifiedFootGroundPosition(hitPosition,
                    out checkedFootDestination, out checkedFootOffset,
                    out failureReason))
                    return false;
                destination = checkedFootDestination;
                offsetMeters = checkedFootOffset;
            }

            if (_waypointTeleportIsVehicle)
            {
                float groundZ;
                Vector3 groundNormal;
                if (!World.GetGroundHeightAndNormal(
                    hitPosition + new Vector3(0f, 0f, 1f),
                    out groundZ, out groundNormal)
                    || !IsFinitePosition(groundNormal)
                    || float.IsNaN(groundZ) || float.IsInfinity(groundZ)
                    || Math.Abs(hitPosition.Z - groundZ) > 1.5f
                    || groundNormal.Z < 0.65f)
                {
                    failureReason = "The vehicle landing surface has no confirmed ground support.";
                    return false;
                }
            }

            float horizontalOffset = HorizontalDistance(
                hitPosition,
                _waypointTeleportWaypoint);
            if (horizontalOffset > WaypointTeleportMaximumOffsetMeters)
            {
                failureReason = "The nearest solid surface is more than 250 m from the waypoint.";
                return false;
            }

            if (_waypointTeleportIsVehicle)
                destination = hitPosition + (surfaceNormal * 0.25f);
            heading = _waypointTeleportIsVehicle
                ? _waypointTeleportEntity.Heading
                : _waypointTeleportPlayer.Heading;
            if (_waypointTeleportIsVehicle)
                offsetMeters = horizontalOffset;
            surfaceNormalZ = surfaceNormal.Z;
            return true;
        }

        private bool IsWaypointFootLandingUnoccupied(Vector3 position,
            out string reason)
        {
            reason = string.Empty;
            foreach (Vehicle vehicle in World.GetNearbyVehicles(position, 1.5f))
            {
                if (vehicle != null && vehicle.Exists()
                    && Math.Abs(vehicle.Position.Z - position.Z) < 2f)
                {
                    reason = "A vehicle occupies the selected walking position.";
                    return false;
                }
            }
            foreach (Ped ped in World.GetNearbyPeds(position, 1.0f))
            {
                if (ped != null && ped.Exists()
                    && (_waypointTeleportPlayer == null
                        || ped.Handle != _waypointTeleportPlayer.Handle)
                    && Math.Abs(ped.Position.Z - position.Z) < 2f)
                {
                    reason = "A Ped occupies the selected walking position.";
                    return false;
                }
            }
            return true;
        }

        private bool TryResolveWaypointTeleportDestination(
            out Vector3 destination,
            out float heading,
            out float offsetMeters,
            out string placement,
            out string failureReason)
        {
            destination = Vector3.Zero;
            heading = 0f;
            offsetMeters = 0f;
            placement = string.Empty;
            failureReason = "No safe destination was found near the waypoint. Nothing was moved.";

            Vector3 groundPosition = _waypointTeleportWaypoint;
            if (!_waypointTeleportIsVehicle
                && Math.Abs(groundPosition.Z) < 0.1f)
                groundPosition.Z = _waypointTeleportHeightHint;
            float groundProbeZ = _waypointTeleportIsVehicle
                ? Math.Max(1000f, _waypointTeleportWaypoint.Z + 100f)
                : _waypointTeleportHeightHint
                    + (_waypointFootGroundAttempts <= 1 ? 60f
                        : _waypointFootGroundAttempts == 2 ? 120f
                        : _waypointFootGroundAttempts == 3 ? 300f : 1000f);
            bool groundFound = false;
            using (OutputArgument groundOutput = new OutputArgument())
            {
                groundFound = Function.Call<bool>(
                    Hash.GET_GROUND_Z_FOR_3D_COORD,
                    _waypointTeleportWaypoint.X,
                    _waypointTeleportWaypoint.Y,
                    groundProbeZ,
                    groundOutput,
                    false);
                if (groundFound)
                {
                    float groundZ = groundOutput.GetResult<float>();
                    if (float.IsNaN(groundZ) || float.IsInfinity(groundZ))
                        groundFound = false;
                    else
                        groundPosition = new Vector3(
                            _waypointTeleportWaypoint.X,
                            _waypointTeleportWaypoint.Y,
                            groundZ);
                }
            }

            if (_waypointTeleportIsVehicle)
            {
                string roadFailure;
                if (!TryResolveWaypointRoadPosition(
                    groundPosition,
                    out destination,
                    out heading,
                    out offsetMeters,
                    out roadFailure))
                {
                    failureReason = roadFailure;
                    return false;
                }

                placement = "NearestRoad";
                return true;
            }

            if (groundFound
                && TryResolveSafePedPosition(
                    groundPosition,
                    out destination,
                    out offsetMeters,
                    out failureReason))
            {
                placement = "SafeGround";
                LogWaypointFootGroundAttempt(groundProbeZ, groundFound,
                    groundPosition, "SafeGround", "SafePed=Accepted");
                return true;
            }

            string safePedFailure = failureReason;
            string directGroundFailure = "not-run";
            if (groundFound
                && TryResolveVerifiedFootGroundPosition(groundPosition,
                    out destination, out offsetMeters, out directGroundFailure))
            {
                placement = "VerifiedGround";
                LogWaypointFootGroundAttempt(groundProbeZ, groundFound,
                    groundPosition, placement,
                    "SafePed=" + safePedFailure + "; DirectGround=" + directGroundFailure);
                return true;
            }

            if (!groundFound
                && TryResolveSafePedPosition(
                    groundPosition,
                    out destination,
                    out offsetMeters,
                    out failureReason))
            {
                placement = "NearbySafePosition";
                LogWaypointFootGroundAttempt(groundProbeZ, groundFound,
                    groundPosition, placement, "SafePed=Accepted");
                return true;
            }

            Vector3 roadPosition;
            float ignoredRoadHeading;
            float roadOffsetMeters;
            string roadPositionFailure;
            if (TryResolveWaypointRoadPosition(
                groundPosition,
                out roadPosition,
                out ignoredRoadHeading,
                out roadOffsetMeters,
                out roadPositionFailure)
                && TryResolveSafePedPosition(
                    roadPosition,
                    out destination,
                    out offsetMeters,
                    out failureReason))
            {
                placement = "RoadsideSafePosition";
                LogWaypointFootGroundAttempt(groundProbeZ, groundFound,
                    groundPosition, placement, "SafePed=Accepted; Road=Confirmed");
                return true;
            }

            failureReason = groundFound
                ? "Ground was found, but neither pedestrian navigation nor the checked foot footprint was safe. Nothing was moved."
                : "The pin has no ground position and no safe nearby walking route was found within 250 m. Nothing was moved.";
            LogWaypointFootGroundAttempt(groundProbeZ, groundFound,
                groundPosition, "Rejected", "SafePed=" + safePedFailure
                + "; DirectGround=" + (groundFound ? directGroundFailure : "not-found")
                + "; Road=" + roadPositionFailure);
            return false;
        }

        private void LogWaypointFootGroundAttempt(float probeZ,
            bool found, Vector3 ground, string result, string detail)
        {
            string normalEvidence = "not-confirmed";
            if (found)
            {
                try
                {
                    float sampleZ;
                    Vector3 normal;
                    if (World.GetGroundHeightAndNormal(
                        ground + new Vector3(0f, 0f, 2f),
                        out sampleZ, out normal))
                        normalEvidence = "Z=" + sampleZ.ToString("0.00", CultureInfo.InvariantCulture)
                            + "; Normal=" + normal;
                }
                catch { normalEvidence = "probe-unavailable"; }
            }
            LogRuntime("DEVELOPER_WAYPOINT_FOOT_GROUND_ATTEMPT",
                "Attempt=" + _waypointFootGroundAttempts
                + "; Probe=(" + _waypointTeleportWaypoint.X.ToString("0.00", CultureInfo.InvariantCulture)
                + "," + _waypointTeleportWaypoint.Y.ToString("0.00", CultureInfo.InvariantCulture)
                + "," + probeZ.ToString("0.00", CultureInfo.InvariantCulture) + ")"
                + "; GroundFound=" + found
                + "; Ground=" + ground
                + "; GroundNormal=" + normalEvidence
                + "; SceneLoaded=true; CollisionRequested=true"
                + "; Result=" + result
                + "; Detail=" + detail);
        }

        private bool TryResolveVerifiedFootGroundPosition(Vector3 ground,
            out Vector3 destination, out float offsetMeters, out string reason)
        {
            destination = Vector3.Zero;
            offsetMeters = 0f;
            reason = "The ground footprint was not confirmed.";
            if (!IsFinitePosition(ground)
                || (Math.Abs(_waypointTeleportWaypoint.Z) >= 0.1f
                    && Math.Abs(ground.Z - _waypointTeleportWaypoint.Z) > 4f))
            {
                reason = "Ground height differs from the selected pin.";
                return false;
            }

            float centerZ;
            Vector3 centerNormal;
            if (!World.GetGroundHeightAndNormal(ground + new Vector3(0f, 0f, 2f),
                out centerZ, out centerNormal)
                || !IsFinitePosition(centerNormal)
                || float.IsNaN(centerZ) || float.IsInfinity(centerZ)
                || centerNormal.Z < WaypointTeleportMinimumFootSurfaceNormalZ
                || Math.Abs(centerZ - ground.Z) > 1f)
            {
                reason = "Ground height or center slope could not be confirmed.";
                return false;
            }

            foreach (Vector3 offset in new[] {
                new Vector3(0.4f, 0f, 0f), new Vector3(-0.4f, 0f, 0f),
                new Vector3(0f, 0.4f, 0f), new Vector3(0f, -0.4f, 0f) })
            {
                float sampleZ;
                Vector3 sampleNormal;
                if (!World.GetGroundHeightAndNormal(
                    ground + offset + new Vector3(0f, 0f, 2f),
                    out sampleZ, out sampleNormal)
                    || !IsFinitePosition(sampleNormal)
                    || float.IsNaN(sampleZ) || float.IsInfinity(sampleZ)
                    || sampleNormal.Z < WaypointTeleportMinimumFootSurfaceNormalZ
                    || Math.Abs(sampleZ - centerZ) > 0.5f)
                {
                    reason = "The nearby foot footprint is steep, uneven, or not loaded.";
                    return false;
                }
            }

            Vector3 position = new Vector3(ground.X, ground.Y, centerZ + 0.08f);
            if (!IsWaypointFootLandingUnoccupied(position, out reason))
                return false;
            offsetMeters = HorizontalDistance(position, _waypointTeleportWaypoint);
            if (offsetMeters > WaypointTeleportMaximumOffsetMeters)
            {
                reason = "The checked ground is more than 250 m from the waypoint.";
                return false;
            }
            destination = position;
            reason = "GroundNormal=" + centerNormal
                + "; FootprintSamples=4/4; OccupancyClear=true";
            return true;
        }

        private bool TryResolveWaypointRoadPosition(
            Vector3 searchPosition,
            out Vector3 roadPosition,
            out float roadHeading,
            out float offsetMeters,
            out string failureReason)
        {
            roadPosition = Vector3.Zero;
            roadHeading = 0f;
            offsetMeters = 0f;
            failureReason = "No usable road was found within 250 m of the waypoint. Nothing was moved.";

            using (OutputArgument nodeOutput = new OutputArgument())
            using (OutputArgument headingOutput = new OutputArgument())
            {
                float zMeasureMultiplier = Math.Abs(searchPosition.Z) < 0.1f ? 0f : 3.0f;
                bool nodeFound = Function.Call<bool>(
                    Hash.GET_CLOSEST_VEHICLE_NODE_WITH_HEADING,
                    searchPosition.X,
                    searchPosition.Y,
                    searchPosition.Z,
                    nodeOutput,
                    headingOutput,
                    1,
                    zMeasureMultiplier,
                    0);
                if (!nodeFound)
                    return false;

                Vector3 foundRoadPosition = nodeOutput.GetResult<Vector3>();
                float foundRoadHeading = headingOutput.GetResult<float>();
                if (!IsFinitePosition(foundRoadPosition)
                    || float.IsNaN(foundRoadHeading)
                    || float.IsInfinity(foundRoadHeading))
                {
                    failureReason = "The road lookup returned an invalid position. Nothing was moved.";
                    return false;
                }

                float horizontalOffset = HorizontalDistance(
                    foundRoadPosition,
                    _waypointTeleportWaypoint);
                if (horizontalOffset > WaypointTeleportMaximumOffsetMeters)
                {
                    failureReason = "The nearest road is more than 250 m from the waypoint. Nothing was moved.";
                    return false;
                }

                float roadGroundZ;
                Vector3 roadNormal;
                if (!World.GetGroundHeightAndNormal(
                    foundRoadPosition + new Vector3(0f, 0f, 1f),
                    out roadGroundZ, out roadNormal)
                    || float.IsNaN(roadGroundZ) || float.IsInfinity(roadGroundZ)
                    || !IsFinitePosition(roadNormal)
                    || Math.Abs(foundRoadPosition.Z - roadGroundZ) > 1.5f
                    || roadNormal.Z < (_waypointTeleportIsVehicle ? 0.75f : 0.65f))
                {
                    failureReason = "The nearby road has no confirmed level ground for arrival. Nothing was moved.";
                    return false;
                }

                roadPosition = new Vector3(
                    foundRoadPosition.X, foundRoadPosition.Y, roadGroundZ);
                roadHeading = foundRoadHeading;
                offsetMeters = horizontalOffset;
                return true;
            }
        }

        private bool TryResolveSafePedPosition(
            Vector3 searchPosition,
            out Vector3 destination,
            out float offsetMeters,
            out string failureReason)
        {
            destination = Vector3.Zero;
            offsetMeters = 0f;
            failureReason = "No safe walking position was found near the waypoint.";

            Vector3 safePosition;
            if (!World.GetSafePositionForPed(
                searchPosition,
                out safePosition,
                (GetSafePositionFlags)0))
                return false;

            if (!IsFinitePosition(safePosition))
            {
                failureReason = "The safe-position lookup returned an invalid position.";
                return false;
            }

            float horizontalOffset = HorizontalDistance(
                safePosition,
                _waypointTeleportWaypoint);
            if (horizontalOffset > WaypointTeleportMaximumOffsetMeters)
            {
                failureReason = "The nearest safe walking position is more than 250 m from the waypoint.";
                return false;
            }

            float safeGroundZ;
            Vector3 safeGroundNormal;
            if (!World.GetGroundHeightAndNormal(
                safePosition + new Vector3(0f, 0f, 2f),
                out safeGroundZ,
                out safeGroundNormal)
                || float.IsNaN(safeGroundZ)
                || float.IsInfinity(safeGroundZ)
                || float.IsNaN(safeGroundNormal.Z)
                || float.IsInfinity(safeGroundNormal.Z)
                || safeGroundNormal.Z < 0.65f
                || Math.Abs(safePosition.Z - safeGroundZ) > 2f)
            {
                failureReason = "The nearby walking position failed ground and slope checks.";
                return false;
            }

            destination = new Vector3(
                safePosition.X,
                safePosition.Y,
                safeGroundZ);
            if (!IsWaypointFootLandingUnoccupied(destination, out failureReason))
            {
                destination = Vector3.Zero;
                return false;
            }
            offsetMeters = horizontalOffset;
            failureReason = string.Empty;
            return true;
        }

        private static bool IsFinitePosition(Vector3 position)
        {
            return !float.IsNaN(position.X) && !float.IsInfinity(position.X)
                && !float.IsNaN(position.Y) && !float.IsInfinity(position.Y)
                && !float.IsNaN(position.Z) && !float.IsInfinity(position.Z);
        }

        private static float HorizontalDistance(Vector3 first, Vector3 second)
        {
            float dx = first.X - second.X;
            float dy = first.Y - second.Y;
            return (float)Math.Sqrt((dx * dx) + (dy * dy));
        }

        private static float SpatialDistance(Vector3 first, Vector3 second)
        {
            float dx = first.X - second.X;
            float dy = first.Y - second.Y;
            float dz = first.Z - second.Z;
            return (float)Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        private string TryRestoreWaypointTeleportOrigin()
        {
            if (!_waypointTeleportMoved
                || _waypointTeleportEntity == null
                || !_waypointTeleportEntity.Exists()
                || _waypointTeleportPlayer == null
                || !_waypointTeleportPlayer.Exists()
                || !IsFinitePosition(_waypointTeleportOrigin))
                return "OriginRestore=Unavailable";

            if (_waypointTeleportIsVehicle)
            {
                Vehicle current = _waypointTeleportPlayer.CurrentVehicle;
                if (current == null || !current.Exists()
                    || current.Handle != _waypointTeleportEntity.Handle)
                    return "OriginRestore=VehicleOccupancyChanged";
                Ped[] occupants;
                try { occupants = current.Occupants; }
                catch { return "OriginRestore=OccupantsUnavailable"; }
                if (_waypointTeleportPassengerHandles.Count > 0
                    && (occupants == null
                        || !_waypointTeleportPassengerHandles.All(handle =>
                            occupants.Any(occupant => occupant != null
                                && occupant.Exists()
                                && occupant.Handle == handle))))
                    return "OriginRestore=PassengerOccupancyChanged";
            }
            else if (_waypointTeleportPlayer.IsInVehicle())
                return "OriginRestore=PlayerEnteredVehicle";

            try
            {
                Function.Call(Hash.REQUEST_COLLISION_AT_COORD,
                    _waypointTeleportOrigin.X,
                    _waypointTeleportOrigin.Y,
                    _waypointTeleportOrigin.Z);
                _waypointTeleportEntity.Position = _waypointTeleportOrigin;
                _waypointTeleportEntity.Heading = _waypointTeleportOriginHeading;
                _waypointTeleportEntity.Velocity = Vector3.Zero;
                bool returned = SpatialDistance(
                    _waypointTeleportEntity.Position,
                    _waypointTeleportOrigin) <= 5f;
                return returned
                    ? "OriginRestore=PositionConfirmed; CollisionAtOrigin=Requested"
                    : "OriginRestore=PositionUnconfirmed";
            }
            catch (Exception error)
            {
                LogException("DEVELOPER_WAYPOINT_ORIGIN_RESTORE_FAILED", error);
                return "OriginRestore=Failed:" + error.GetType().Name;
            }
        }

        private void FinishWaypointTeleport(
            bool succeeded,
            string context,
            string notificationMessage = null)
        {
            if (_waypointTeleportStage == WaypointTeleportStage.None)
                return;

            bool movedVehicle = _waypointTeleportIsVehicle;
            float offsetMeters = _waypointTeleportOffsetMeters;
            bool stopSceneLoad = _waypointTeleportSceneLoadRequested;
            if (!succeeded && _waypointTeleportMoved)
                context = (context ?? string.Empty) + "; "
                    + TryRestoreWaypointTeleportOrigin();
            try
            {
                RestoreWaypointTeleportTransition(stopSceneLoad);
            }
            finally
            {
                _waypointTeleportStage = WaypointTeleportStage.None;
                _waypointTeleportEntity = null;
                _waypointTeleportPlayer = null;
                _waypointTeleportWaypoint = Vector3.Zero;
                _waypointTeleportHeightHint = 0f;
                _waypointTeleportOrigin = Vector3.Zero;
                _waypointTeleportOriginHeading = 0f;
                _waypointTeleportDestination = Vector3.Zero;
                _waypointTeleportDestinationHeading = 0f;
                _waypointTeleportOffsetMeters = 0f;
                _waypointTeleportSurfaceNormalZ = 0f;
                _waypointTeleportPlacement = string.Empty;
                _waypointTeleportNextPollUtc = DateTime.MinValue;
                _waypointSurfaceProbeNextStartUtc = DateTime.MinValue;
                _waypointTeleportDeadlineUtc = DateTime.MinValue;
                _waypointTeleportLandingDeadlineUtc = DateTime.MinValue;
                _waypointSurfaceProbeHandle = 0;
                _waypointSurfaceProbeAttempts = 0;
                _waypointSurfaceProbeOrigin = Vector3.Zero;
                _waypointSurfaceProbeEnd = Vector3.Zero;
                _waypointFootGroundAttempts = 0;
                _waypointFootGroundNextAttemptUtc = DateTime.MinValue;
                _waypointSurfaceProbeFailure = string.Empty;
                _waypointTeleportPassengerHandles.Clear();
                _waypointTeleportIsVehicle = false;
                _waypointTeleportSceneLoadRequested = false;
                _waypointTeleportDestinationResolved = false;
                _waypointTeleportMoved = false;
                _waypointSurfaceProbeCompleted = false;
            }

            LogRuntime(
                succeeded
                    ? "DEVELOPER_WAYPOINT_TELEPORT_COMPLETED"
                    : "DEVELOPER_WAYPOINT_TELEPORT_CANCELLED",
                context ?? string.Empty);
            string successMessage = movedVehicle
                ? "~b~WAYPOINT REACHED~s~~n~Vehicle and occupants stayed together."
                : "~b~WAYPOINT REACHED~s~~n~Arrived on a safe surface.";
            if (offsetMeters >= 10f)
                successMessage += "~n~Arrival point: "
                    + ((int)Math.Round(offsetMeters)).ToString(CultureInfo.InvariantCulture)
                    + " m from the pin.";
            Notify(succeeded
                ? successMessage
                : (notificationMessage ?? context ?? "Waypoint move cancelled."));
        }

        private void CancelWaypointTeleport(string reason, bool notify)
        {
            if (_waypointTeleportStage == WaypointTeleportStage.None)
                return;

            bool stopSceneLoad = _waypointTeleportSceneLoadRequested;
            if (_waypointTeleportMoved)
                reason = (reason ?? "Waypoint move cancelled.") + "; "
                    + TryRestoreWaypointTeleportOrigin();
            try
            {
                RestoreWaypointTeleportTransition(stopSceneLoad);
            }
            finally
            {
                _waypointTeleportStage = WaypointTeleportStage.None;
                _waypointTeleportEntity = null;
                _waypointTeleportPlayer = null;
                _waypointTeleportWaypoint = Vector3.Zero;
                _waypointTeleportHeightHint = 0f;
                _waypointTeleportOrigin = Vector3.Zero;
                _waypointTeleportOriginHeading = 0f;
                _waypointTeleportDestination = Vector3.Zero;
                _waypointTeleportDestinationHeading = 0f;
                _waypointTeleportOffsetMeters = 0f;
                _waypointTeleportSurfaceNormalZ = 0f;
                _waypointTeleportPlacement = string.Empty;
                _waypointTeleportNextPollUtc = DateTime.MinValue;
                _waypointSurfaceProbeNextStartUtc = DateTime.MinValue;
                _waypointTeleportDeadlineUtc = DateTime.MinValue;
                _waypointTeleportLandingDeadlineUtc = DateTime.MinValue;
                _waypointSurfaceProbeHandle = 0;
                _waypointSurfaceProbeAttempts = 0;
                _waypointSurfaceProbeOrigin = Vector3.Zero;
                _waypointSurfaceProbeEnd = Vector3.Zero;
                _waypointFootGroundAttempts = 0;
                _waypointFootGroundNextAttemptUtc = DateTime.MinValue;
                _waypointSurfaceProbeFailure = string.Empty;
                _waypointTeleportPassengerHandles.Clear();
                _waypointTeleportIsVehicle = false;
                _waypointTeleportSceneLoadRequested = false;
                _waypointTeleportDestinationResolved = false;
                _waypointTeleportMoved = false;
                _waypointSurfaceProbeCompleted = false;
            }

            LogRuntime(
                "DEVELOPER_WAYPOINT_TELEPORT_CANCELLED",
                reason ?? "Waypoint move cancelled.");
            if (notify)
                Notify(reason ?? "Waypoint move cancelled.");
        }

        private void RestoreWaypointTeleportTransition(bool stopSceneLoad)
        {
            if (stopSceneLoad)
            {
                try
                {
                    Function.Call(Hash.NEW_LOAD_SCENE_STOP);
                }
                catch (Exception error)
                {
                    LogException("DEVELOPER_WAYPOINT_TELEPORT_SCENE_STOP_FAILED", error);
                }
            }

            try
            {
                Function.Call(Hash.DO_SCREEN_FADE_IN,
                    WaypointTeleportFadeInMilliseconds);
            }
            catch (Exception error)
            {
                LogException("DEVELOPER_WAYPOINT_TELEPORT_FADE_RESTORE_FAILED", error);
            }
        }

        private void ToggleDispatchSceneInspection()
        {
            if (!_settings.Enabled || !_settings.DeveloperMenuEnabled)
            {
                Notify("Developer Mode is disabled in LSDeveloper.xml.");
                return;
            }

            _settings.CheckDispatchScenes = !_settings.CheckDispatchScenes;
            if (!_settings.CheckDispatchScenes)
                ClearDispatchSceneInspectionBlips();
            try
            {
                _settings.Save(_configurationPath);
                LogRuntime("DEVELOPER_DISPATCH_SCENE_CHECK_TOGGLED",
                    "Enabled=" + _settings.CheckDispatchScenes
                    + "; ExistingOccurrenceContinues=true; HiddenPreparationRetained=true");
                Notify(_settings.CheckDispatchScenes
                    ? "Dispatch scene check enabled. Request a Dispatch to inspect preparation."
                    : "Dispatch scene check disabled. The current Dispatch preparation continues safely.");
            }
            catch (Exception error)
            {
                LogException("DEVELOPER_DISPATCH_SCENE_CHECK_SAVE_FAILED", error);
                Notify("Dispatch scene check setting could not be saved.");
            }
            RefreshMenuControls();
        }

        private void RefreshDispatchSceneInspectionMenu()
        {
            if (_checkDispatchScenesItem != null)
            {
                _checkDispatchScenesItem.Title = "Check Dispatch Scenes: "
                    + (_settings.CheckDispatchScenes ? "ON" : "OFF");
                _checkDispatchScenesItem.Description =
                    "Inspect the live Dispatch preparation. Prepared participants stay hidden until the offer is published.";
            }
            if (_dispatchSceneInspectionStatusItem != null
                && !_settings.CheckDispatchScenes)
                _dispatchSceneInspectionStatusItem.Description = "Inspection is disabled.";
        }

        internal void ObserveDispatchSceneUpdate(LSDeveloperDispatchSceneUpdate update)
        {
            if (update == null || _shutDown || !_settings.Enabled
                || !_settings.DeveloperMenuEnabled || !_settings.CheckDispatchScenes)
                return;

            AddManualTrace(new LSDeveloperTraceEvent
            {
                TimestampUtc = update.TimestampUtc == DateTime.MinValue
                    ? DateTime.UtcNow : update.TimestampUtc,
                RuntimeSessionId = _log == null ? string.Empty : _log.SessionId,
                Owner = "LSPDDispatch",
                Category = "DEVELOPER_DISPATCH_SCENE_"
                    + (update.Stage ?? "UNKNOWN").ToUpperInvariant(),
                EventKind = "DispatchPreparationStage",
                ActorHandle = update.ActorHandles == null || update.ActorHandles.Count == 0
                    ? 0 : update.ActorHandles[0],
                Reason = update.Reason ?? update.FailureStage ?? string.Empty,
                Context = FormatDispatchSceneUpdate(update)
            });

            lock (_sync)
            {
                while (_dispatchSceneUpdates.Count >= 64)
                    _dispatchSceneUpdates.Dequeue();
                _dispatchSceneUpdates.Enqueue(update.Clone());
            }
        }

        private void ProcessDispatchSceneInspection()
        {
            if (!_settings.CheckDispatchScenes)
            {
                lock (_sync)
                    _dispatchSceneUpdates.Clear();
                if (_dispatchInspectionBlips.Count > 0)
                    ClearDispatchSceneInspectionBlips();
                return;
            }

            List<LSDeveloperDispatchSceneUpdate> updates = new List<LSDeveloperDispatchSceneUpdate>();
            lock (_sync)
            {
                while (_dispatchSceneUpdates.Count > 0 && updates.Count < 16)
                    updates.Add(_dispatchSceneUpdates.Dequeue());
            }

            foreach (LSDeveloperDispatchSceneUpdate update in updates)
            {
                if (update == null)
                    continue;
                if (string.Equals(update.Stage, "SearchStarted", StringComparison.OrdinalIgnoreCase))
                {
                    ClearDispatchSceneInspectionBlips();
                    _dispatchInspectionSearchId = update.SearchId ?? string.Empty;
                    _dispatchInspectionOccurrenceId = string.Empty;
                    SetDispatchSceneInspectionStatus(
                        "Searching catalog",
                        "Dispatch is checking compatible catalog locations.");
                    continue;
                }

                if (string.Equals(update.Stage, "LocationFound", StringComparison.OrdinalIgnoreCase))
                {
                    ClearDispatchSceneInspectionBlips();
                    _dispatchInspectionSearchId = update.SearchId ?? string.Empty;
                    _dispatchInspectionOccurrenceId = update.OccurrenceId ?? string.Empty;
                    string locationKind = update.IsInterior
                        ? "Interior" : update.RequiresVehicle ? "Vehicle" : "Exterior";
                    string locationName = string.IsNullOrWhiteSpace(update.LocationName)
                        ? update.LocationId : update.LocationName;
                    string label = "Catalog location: " + locationName
                        + " [" + update.LocationId + "] | " + locationKind
                        + " | " + update.Position;
                    CreateDispatchInspectionBlip(update.Position, BlipColor.Green, label);
                    SetDispatchSceneInspectionStatus(
                        "Location found",
                        "Location found. Preparing incident. " + Limit(label, 260));
                    LogRuntime("DEVELOPER_DISPATCH_SCENE_LOCATION_FOUND",
                        FormatDispatchSceneUpdate(update) + "; Marker=Green");
                    Notify("Location found. Preparing incident.");
                    continue;
                }

                if (string.Equals(update.Stage, "ProfilesSelected", StringComparison.OrdinalIgnoreCase))
                {
                    SetDispatchSceneInspectionStatus(
                        "Profiles selected",
                        "Criminal profile: " + update.CriminalProfileId
                        + "; civilian pool: " + update.VictimPoolId
                        + "; scene participant pool: " + update.SceneParticipantPoolId);
                    LogRuntime("DEVELOPER_DISPATCH_SCENE_PROFILES_SELECTED",
                        FormatDispatchSceneUpdate(update));
                    continue;
                }

                if (string.Equals(update.Stage, "ActorsStaged", StringComparison.OrdinalIgnoreCase))
                {
                    int validActors = 0;
                    for (int index = 0; index < update.ActorHandles.Count; index++)
                    {
                        int handle = update.ActorHandles[index];
                        Ped actor = null;
                        try { actor = Entity.FromHandle(handle) as Ped; } catch { }
                        if (actor == null || !actor.Exists())
                        {
                            LogDebug("DEVELOPER_DISPATCH_SCENE_ACTOR_REGISTRATION_FAILED",
                                FormatDispatchSceneUpdate(update) + "; Handle=" + handle
                                + "; Result=PedEntityNotResolvable");
                            continue;
                        }

                        validActors++;
                        string role = index < update.ActorRoles.Count
                            ? update.ActorRoles[index] : "Scene participant";
                        string actorEvidence;
                        try
                        {
                            int interiorId = Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, actor);
                            bool visible = Function.Call<bool>(Hash.IS_ENTITY_VISIBLE, actor);
                            actorEvidence = "Role=" + role + "; Handle=" + actor.Handle
                                + "; ModelHash=" + actor.Model.Hash
                                + "; Position=" + actor.Position
                                + "; EntityInteriorId=" + interiorId
                                + "; Visible=" + visible
                                + "; Frozen=" + actor.IsPositionFrozen
                                + "; BlocksAmbientEvents=" + actor.BlockPermanentEvents;
                        }
                        catch (Exception error)
                        {
                            actorEvidence = "Role=" + role + "; Handle=" + actor.Handle
                                + "; Position=" + actor.Position
                                + "; RegistrationProbe=" + error.GetType().Name;
                        }
                        LogRuntime("DEVELOPER_DISPATCH_SCENE_ACTOR_REGISTERED",
                            FormatDispatchSceneUpdate(update) + "; " + actorEvidence);
                        if (!_dispatchInspectionPedBlips.ContainsKey(actor.Handle))
                        {
                            try
                            {
                                Blip pedBlip = actor.AddBlip();
                                if (pedBlip != null && pedBlip.Exists())
                                {
                                    pedBlip.Color = BlipColor.Blue;
                                    pedBlip.Name = role + " | " + actor.Handle;
                                    pedBlip.IsShortRange = false;
                                    _dispatchInspectionPedBlips[actor.Handle] = pedBlip;
                                }
                            }
                            catch (Exception error)
                            {
                                LogDebug("DEVELOPER_DISPATCH_SCENE_PED_BLIP_FAILED",
                                    "Handle=" + actor.Handle + "; Reason=" + error.GetType().Name);
                            }
                        }
                    }

                    if (validActors == update.ActorHandles.Count && validActors > 0)
                    {
                        foreach (Blip marker in _dispatchInspectionBlips)
                        {
                            try
                            {
                                if (marker != null && marker.Exists())
                                    marker.Color = BlipColor.Blue;
                            }
                            catch { }
                        }
                    }
                    SetDispatchSceneInspectionStatus(
                        "Participants staged",
                        "Pedestrians staged: " + validActors + "/" + update.RequiredActorCount
                        + ". Entity handles and their live positions were checked.");
                    LogRuntime("DEVELOPER_DISPATCH_SCENE_ACTORS_STAGED",
                        FormatDispatchSceneUpdate(update)
                        + "; ActualPedEntities=" + validActors
                        + "; Required=" + update.RequiredActorCount
                        + "; ActorsCreated=" + update.ActorsCreated
                        + "; ActorsReused=" + update.ActorsReused
                        + "; Marker=Blue");
                    Notify("Pedestrians staged. Checking incident.");
                    continue;
                }

                if (string.Equals(update.Stage, "BranchesPrepared", StringComparison.OrdinalIgnoreCase))
                {
                    CreateDispatchInspectionBlip(
                        update.Position,
                        BlipColor.Yellow,
                        "Dispatch event: " + update.EventTitle + " [" + update.EventId + "]");
                    SetDispatchSceneInspectionStatus(
                        "Event branches prepared",
                        update.EventTitle + " [" + update.EventId + "]; Branches: "
                        + string.Join(", ", update.BranchIds)
                        + "; Pre-arrival: " + update.PreArrivalActivity
                        + "; Suspect responses: " + string.Join(", ", update.SuspectResponseBranches)
                        + "; Victim responses: " + string.Join(", ", update.VictimResponseBranches));
                    LogRuntime("DEVELOPER_DISPATCH_SCENE_BRANCHES_PREPARED",
                        FormatDispatchSceneUpdate(update)
                        + "; Branches=" + string.Join(",", update.BranchIds)
                        + "; SuspectResponseBranches=" + string.Join(",", update.SuspectResponseBranches)
                        + "; VictimResponseBranches=" + string.Join(",", update.VictimResponseBranches)
                        + "; PreArrivalActivity=" + update.PreArrivalActivity
                        + "; Marker=Yellow");
                    Notify("Dispatch event and behavior branches prepared.");
                    continue;
                }

                if (string.Equals(update.Stage, "PreparationPending", StringComparison.OrdinalIgnoreCase))
                {
                    string pendingContext = string.IsNullOrWhiteSpace(update.OccurrenceId)
                        ? "The catalog search candidate remains pending."
                        : "The same occurrence is retained.";
                    SetDispatchSceneInspectionStatus(
                        "Preparation pending",
                        "Still waiting for supported scene readiness: " + Limit(update.Reason, 260)
                        + ". " + pendingContext);
                    LogDebug("DEVELOPER_DISPATCH_SCENE_PREPARATION_PENDING",
                        FormatDispatchSceneUpdate(update));
                    continue;
                }

                if (string.Equals(update.Stage, "CandidateFailed", StringComparison.OrdinalIgnoreCase))
                {
                    ClearDispatchSceneInspectionBlips();
                    _dispatchInspectionOccurrenceId = string.Empty;
                    string stage = HumanDispatchFailureStage(update.FailureStage, update.Reason);
                    SetDispatchSceneInspectionStatus(
                        "Candidate failed",
                        "Scene check failed: " + stage + ". Dispatch is continuing its existing search.");
                    LogDebug("DEVELOPER_DISPATCH_SCENE_CANDIDATE_FAILED",
                        FormatDispatchSceneUpdate(update)
                        + "; FirstFailedStage=" + stage
                        + "; TechnicalReason=" + update.Reason);
                    string signature = (update.SearchId ?? string.Empty) + ":"
                        + update.CandidateAttempt + ":" + (update.FailureStage ?? string.Empty);
                    DateTime now = DateTime.UtcNow;
                    if (!string.Equals(signature, _lastInspectionFailureSignature,
                            StringComparison.OrdinalIgnoreCase)
                        && now - _lastInspectionFailureNoticeUtc >= TimeSpan.FromMilliseconds(1500))
                    {
                        _lastInspectionFailureSignature = signature;
                        _lastInspectionFailureNoticeUtc = now;
                        Notify("Scene check failed: " + stage + ". Dispatch is continuing its search.");
                    }
                    continue;
                }

                if (string.Equals(update.Stage, "OfferPublished", StringComparison.OrdinalIgnoreCase))
                {
                    _dispatchInspectionOccurrenceId = update.OccurrenceId ?? string.Empty;
                    CreateDispatchInspectionBlip(
                        update.Position,
                        BlipColor.Orange,
                        "SCENE READY — OFFER PUBLISHED: " + update.EventTitle
                        + " [" + update.OccurrenceId + "]");
                    SetDispatchSceneInspectionStatus(
                        "Scene ready — offer published",
                        update.EventTitle + " at " + update.LocationName
                        + ". The prepared participants were released by Dispatch.");
                    LogRuntime("DEVELOPER_DISPATCH_SCENE_OFFER_PUBLISHED",
                        FormatDispatchSceneUpdate(update) + "; Marker=Orange");
                    continue;
                }

                if (string.Equals(update.Stage, "OfferAccepted", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(update.Stage, "OccurrenceEnded", StringComparison.OrdinalIgnoreCase))
                {
                    ClearDispatchSceneInspectionBlips();
                    _dispatchInspectionOccurrenceId = string.Empty;
                    SetDispatchSceneInspectionStatus(
                        string.Equals(update.Stage, "OfferAccepted", StringComparison.OrdinalIgnoreCase)
                            ? "Offer accepted" : "Inspection occurrence ended",
                        "Temporary Developer markers were cleared; normal Dispatch ownership continues.");
                    LogRuntime("DEVELOPER_DISPATCH_SCENE_INSPECTION_CLEARED",
                        FormatDispatchSceneUpdate(update));
                }
            }
        }

        private void CreateDispatchInspectionBlip(Vector3 position, BlipColor color, string name)
        {
            try
            {
                Blip marker = World.CreateBlip(position);
                if (marker == null || !marker.Exists())
                    return;
                marker.Color = color;
                marker.Name = Limit(name, 100);
                marker.IsShortRange = false;
                _dispatchInspectionBlips.Add(marker);
            }
            catch (Exception error)
            {
                LogDebug("DEVELOPER_DISPATCH_SCENE_BLIP_FAILED",
                    "Color=" + color + "; Reason=" + error.GetType().Name);
            }
        }

        private void ClearDispatchSceneInspectionBlips()
        {
            foreach (Blip blip in _dispatchInspectionBlips.ToArray())
                DeleteDispatchInspectionBlip(blip);
            foreach (Blip blip in _dispatchInspectionPedBlips.Values.ToArray())
                DeleteDispatchInspectionBlip(blip);
            _dispatchInspectionBlips.Clear();
            _dispatchInspectionPedBlips.Clear();
        }

        private static void DeleteDispatchInspectionBlip(Blip blip)
        {
            try
            {
                if (blip != null && blip.Exists())
                    blip.Delete();
            }
            catch { }
        }

        private void SetDispatchSceneInspectionStatus(string title, string description)
        {
            if (_dispatchSceneInspectionStatusItem == null)
                return;
            _dispatchSceneInspectionStatusItem.Title = "Dispatch Scene Check: " + Limit(title, 80);
            _dispatchSceneInspectionStatusItem.Description = Limit(description, 360);
        }

        private static string FormatDispatchSceneUpdate(LSDeveloperDispatchSceneUpdate update)
        {
            return "Search=" + (update.SearchId ?? string.Empty)
                + "; Occurrence=" + (update.OccurrenceId ?? string.Empty)
                + "; CandidateAttempt=" + update.CandidateAttempt
                + "; Stage=" + (update.Stage ?? string.Empty)
                + "; Event=" + (update.EventId ?? string.Empty)
                + "; Location=" + (update.LocationId ?? string.Empty)
                + "; LocationName=" + (update.LocationName ?? string.Empty)
                + "; Point=" + (update.PointId ?? string.Empty)
                + "; Position=" + update.Position
                + "; CriminalProfile=" + (update.CriminalProfileId ?? string.Empty)
                + "; VictimPool=" + (update.VictimPoolId ?? string.Empty)
                + "; ParticipantPool=" + (update.SceneParticipantPoolId ?? string.Empty)
                + "; ActorsCreated=" + update.ActorsCreated
                + "; ActorsReused=" + update.ActorsReused
                + "; ActorHandles=" + string.Join(",", update.ActorHandles ?? new List<int>())
                + "; ActorRoles=" + string.Join(",", update.ActorRoles ?? new List<string>())
                + "; Branches=" + string.Join(",", update.BranchIds ?? new List<string>())
                + "; SuspectBranches=" + string.Join(",",
                    update.SuspectResponseBranches ?? new List<string>())
                + "; VictimBranches=" + string.Join(",",
                    update.VictimResponseBranches ?? new List<string>())
                + "; FailureStage=" + (update.FailureStage ?? string.Empty)
                + "; Reason=" + (update.Reason ?? string.Empty)
                + "; TimestampUtc=" + update.TimestampUtc.ToString(
                    "O", CultureInfo.InvariantCulture);
        }

        private static string HumanDispatchFailureStage(string stage, string reason)
        {
            string value = (stage ?? string.Empty) + " " + (reason ?? string.Empty);
            if (value.IndexOf("InteriorActorIdentity", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("InteriorActorRoom", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("InteriorActorFloor", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("InteriorFloorAlignment", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("InteriorPhysicalDoor", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("InteriorAccess", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("InteriorRegistration", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("RoomRegistration", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("DoorCandidate", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("Doorway", StringComparison.OrdinalIgnoreCase) >= 0)
                return "interior access or registration";
            if (value.IndexOf("Ground", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("Collision", StringComparison.OrdinalIgnoreCase) >= 0)
                return "physical ground readiness";
            if (value.IndexOf("Placement", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("Navigation", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("Roster", StringComparison.OrdinalIgnoreCase) >= 0)
                return "participant placement";
            if (value.IndexOf("Vehicle", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("Seat", StringComparison.OrdinalIgnoreCase) >= 0)
                return "vehicle or seat capacity";
            if (value.IndexOf("Model", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("Profile", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("Victim", StringComparison.OrdinalIgnoreCase) >= 0)
                return "participant profile or model";
            if (value.IndexOf("CatalogLocation", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("LocationResolution", StringComparison.OrdinalIgnoreCase) >= 0)
                return "catalog location resolution";
            if (value.IndexOf("CatalogCandidate", StringComparison.OrdinalIgnoreCase) >= 0)
                return "catalog candidate selection";
            if (value.IndexOf("Branch", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("Event", StringComparison.OrdinalIgnoreCase) >= 0)
                return "event branch compatibility";
            if ((stage ?? string.Empty).StartsWith(
                    "Interior", StringComparison.OrdinalIgnoreCase)
                || (stage ?? string.Empty).IndexOf(
                    "Door", StringComparison.OrdinalIgnoreCase) >= 0)
                return "interior access or registration";
            return string.IsNullOrWhiteSpace(stage) ? "scene preparation" : Limit(stage, 70);
        }

        private void StartSelectedBranchTest()
        {
            if (_testEventItem == null || _branchTestItem == null)
            {
                Notify("The Developer branch choices are not available.");
                return;
            }

            StartTest(_testEventItem.SelectedItem, SelectedBranchName());
        }

        private void StartTest(string eventName, string branchName)
        {
            if (!_settings.Enabled || !_settings.DeveloperMenuEnabled)
            {
                Notify("Developer Mode is disabled in LSDeveloper.xml.");
                return;
            }

            if (_activeTest != null)
            {
                Notify("Conclude or end the current Branch Test before starting another one.");
                return;
            }

            LSDeveloperBranchDefinition branch = _branchCatalog.Find(eventName, branchName);
            if (branch == null)
            {
                Notify("The selected documented Branch Test is unavailable.");
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
                + "; TestEvent=" + branch.EventName
                + "; Branch=" + branch.Name
                + "; Source=" + branch.SourceSection + " lines " + branch.SourceLines
                + "; Expected=" + branch.ExpectedBehavior);
            Notify("Branch Test started: " + branch.Name + ". Perform one controlled branch only.");
            RefreshMenuControls();
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
            EndTestInternal("Player concluded the test at " + checkpoint.Name, false);
            Notify("Test ended; final checkpoint saved. Select Show Checkpoint Test to review.");
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
                Notify("Test ended. Select Show Checkpoint Test to review.");

            RefreshMenuControls();
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
                bool readableReportWritten = WriteHumanReadableReport(_lastAnalysis);
                RefreshResultView(_lastAnalysis, readableReportWritten);
                LogRuntime("DEVELOPER_TEST_ANALYZED", _lastAnalysis.Summary);
                LogDebug("DEVELOPER_FIRST_DIVERGENCE", _lastAnalysis.FirstDivergence);
                OpenResultView();
                Notify(AnalysisNotification(readableReportWritten));
            }
            catch (Exception error)
            {
                LogException("DEVELOPER_TEST_ANALYSIS_FAILED", error);
                Notify("Test analysis could not be displayed. Check LSDebug.log for the error.");
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
                Event = session.Branch.EventName,
                Branch = session.Branch.Name,
                ExpectedBehavior = session.Branch.ExpectedBehavior,
                FlowConnection = session.Branch.FlowConnection,
                BranchSource = session.Branch.SourceSection + " lines " + session.Branch.SourceLines,
                Events = events
            };

            List<LSDeveloperExpectedStep> expectedSteps = session.Branch.Steps;
            bool routeDecisionPending = false;
            bool routeDecisionAmbiguous = false;
            if (session.Branch.Routes.Count == 1)
            {
                LSDeveloperExpectedRoute route = session.Branch.Routes[0];
                expectedSteps = route.Steps;
                result.SelectedRoute = route.Name;
            }
            else if (session.Branch.Routes.Count > 1)
            {
                List<LSDeveloperExpectedRoute> observedRoutes = session.Branch.Routes
                    .Where(route => route.SelectorCategories.Any(category => events.Any(trace =>
                        string.Equals(trace.Category, category, StringComparison.OrdinalIgnoreCase))))
                    .ToList();

                if (observedRoutes.Count == 1)
                {
                    LSDeveloperExpectedRoute route = observedRoutes[0];
                    expectedSteps = route.Steps;
                    result.SelectedRoute = route.Name;
                    result.ExpectedBehavior = (result.ExpectedBehavior ?? string.Empty)
                        + " Observed documented outcome: " + route.Name + ".";
                }
                else
                {
                    expectedSteps = CommonRoutePrefix(session.Branch.Routes);
                    routeDecisionPending = observedRoutes.Count == 0;
                    routeDecisionAmbiguous = observedRoutes.Count > 1;
                    result.SelectedRoute = routeDecisionPending
                        ? "Not determined; the documented outcome marker was not observed."
                        : "Ambiguous; multiple documented outcome markers were observed.";
                    result.ExpectedBehavior = (result.ExpectedBehavior ?? string.Empty)
                        + " Documented outcomes: "
                        + string.Join("; ", session.Branch.Routes.Select(route => route.Name).ToArray()) + ".";
                }
            }

            result.ExpectedStepCount = expectedSteps.Count;
            int expectedIndex = 0;
            LSDeveloperTraceEvent firstProblem = null;
            for (int i = 0; i < events.Count; i++)
            {
                LSDeveloperTraceEvent trace = events[i];
                if (expectedIndex < expectedSteps.Count
                    && expectedSteps[expectedIndex].Matches(trace))
                {
                    result.MatchedSteps.Add(expectedSteps[expectedIndex].Name);
                    expectedIndex++;
                    continue;
                }

                if (firstProblem == null
                    && (trace.EventKind == "TaskConflict"
                        || trace.EventKind == "PhysicalMismatch"
                        || IsSerious(trace)))
                {
                    firstProblem = trace;
                    result.FirstProblemStep = expectedIndex < expectedSteps.Count
                        ? expectedSteps[expectedIndex].Name
                        : "post-branch observation";
                }
            }

            if (firstProblem != null)
            {
                result.FirstDivergenceEvent = firstProblem;
                result.FirstDivergence = DescribeFirstProblem(firstProblem, result.FirstProblemStep, session);
                result.Classification = firstProblem.EventKind == "TaskConflict"
                    || firstProblem.EventKind == "PhysicalMismatch"
                    || firstProblem.EventKind == "BehaviorStall"
                    ? "CONFIRMED_ANOMALY"
                    : "CONFIRMED_FAILURE_EVIDENCE";
                result.Inferences.Add("The first divergence is anchored to the earliest serious/conflict/mismatch event in the bounded trace, not the last error.");
            }
            else if (routeDecisionAmbiguous)
            {
                result.FirstDivergence = "More than one documented outcome marker was observed; the expected route cannot be selected safely.";
                result.Classification = "UNKNOWN";
                result.Unknown.Add("Alternative route selection is ambiguous. Review the route markers and timeline; Developer Mode did not choose a gameplay outcome.");
            }
            else if (expectedIndex < expectedSteps.Count)
            {
                string missing = expectedSteps[expectedIndex].Name;
                result.FirstDivergence = "Expected step was not confirmed before the test ended or analysis was requested: " + missing + ".";
                result.Classification = session.IsEnded ? "OBSERVED_INCOMPLETE" : "UNKNOWN";
                result.Inferences.Add("The trace contains no serious event proving the cause of the missing step.");
            }
            else if (routeDecisionPending)
            {
                result.FirstDivergence = "The shared documented steps were observed, but no route-specific outcome was captured; the test ended before the documented branch could be compared.";
                result.Classification = session.IsEnded ? "OBSERVED_INCOMPLETE" : "UNKNOWN";
                result.Unknown.Add("The documented flow has multiple outcomes. No route-selection category was recorded, so no outcome was assumed.");
            }
            else if (expectedSteps.Count == 0)
            {
                result.FirstDivergence = "No documented expected route is attached to this selection, so the captured evidence cannot be compared as a pass or failure.";
                result.Classification = session.IsEnded ? "INCOMPLETE" : "UNKNOWN";
                result.Unknown.Add("The available official documents do not provide an expected route for this selection. Review captured evidence without treating it as a pass or gameplay failure.");
            }
            else
            {
                result.FirstDivergence = "No divergence was observed in the available evidence for the selected branch.";
                result.Classification = "NO_DIVERGENCE_OBSERVED";
            }

            result.ConfirmedFacts.Add("TestId=" + session.TestId
                + "; TestEvent=" + session.Branch.EventName
                + "; Branch=" + session.Branch.Name
                + "; Source=" + session.Branch.SourceSection + " lines " + session.Branch.SourceLines);
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
                + "; TestEvent=" + session.Branch.EventName
                + "; Branch=" + session.Branch.Name
                + "; Classification=" + result.Classification
                + "; Events=" + events.Count.ToString(CultureInfo.InvariantCulture)
                + "; FirstDivergence=" + result.FirstDivergence;
            return result;
        }

        private static List<LSDeveloperExpectedStep> CommonRoutePrefix(
            IList<LSDeveloperExpectedRoute> routes)
        {
            List<LSDeveloperExpectedStep> prefix = new List<LSDeveloperExpectedStep>();
            if (routes == null || routes.Count == 0)
                return prefix;

            List<LSDeveloperExpectedStep> first = routes[0].Steps;
            for (int i = 0; i < first.Count; i++)
            {
                LSDeveloperExpectedStep step = first[i];
                bool shared = routes.Skip(1).All(route => route.Steps.Count > i
                    && string.Equals(route.Steps[i].Name, step.Name, StringComparison.OrdinalIgnoreCase)
                    && route.Steps[i].Categories.SequenceEqual(step.Categories, StringComparer.OrdinalIgnoreCase));
                if (!shared)
                    break;
                prefix.Add(step);
            }
            return prefix;
        }

        private string DescribeFirstProblem(
            LSDeveloperTraceEvent problem,
            string expectedStep,
            LSDeveloperTestSession session)
        {
            if (problem.Category.IndexOf(
                    "INTERIOR_ACTOR_PREFLIGHT", StringComparison.OrdinalIgnoreCase) >= 0
                && problem.EventKind == "PhysicalMismatch")
            {
                string recordedEvidence = DescribeEvent(problem);
                bool coordinateOnly = recordedEvidence.IndexOf(
                    "EntityIdentityRejectedCoordinateFallback", StringComparison.OrdinalIgnoreCase) >= 0
                    || recordedEvidence.IndexOf(
                        "ActorEntityOutsideSelectedInterior", StringComparison.OrdinalIgnoreCase) >= 0;
                return "FIRST DIVERGENCE: the mapped interior actor readiness check rejected the scene before the Dispatch offer. "
                    + (coordinateOnly
                        ? "Coordinate lookup selected an interior at the actor position, but the actor entity itself was not confirmed in that live interior; a point near a doorway can therefore look interior by coordinates while the Ped remains outside. "
                        : string.Empty)
                    + "The gameplay owners remain in control; Developer Mode reports only recorded role, position, interior, room, floor, and mapped doorway evidence. "
                    + "Mapped doorway resolution is catalog access evidence, not proof of a completed Ped navmesh path. Any further cause is unknown. Evidence="
                    + recordedEvidence;
            }
            if (problem.EventKind == "BehaviorStall")
            {
                return "FIRST DIVERGENCE: expected continuation '"
                    + ValueOrUnknown(problem.ExpectedNext)
                    + "' was not confirmed after the selected actor remained in the observed logical/physical condition. "
                    + "This confirms a behavioral stall, not the hidden GTA cause. Evidence="
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
            output.AppendLine("TEST EVENT=" + Sanitize(result.Event));
            output.AppendLine("BRANCH TEST=" + Sanitize(result.Branch));
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

        private void RefreshResultView(
            LSDeveloperAnalysisResult result,
            bool readableReportWritten)
        {
            if (result == null)
                return;

            List<LSDeveloperTraceEvent> events = result.Events ?? new List<LSDeveloperTraceEvent>();
            LSDeveloperTraceEvent focus = SelectReportEvent(result, events);
            LSDeveloperTraceEvent stateEvidence = LatestReportEvent(events, focus,
                item => !string.IsNullOrWhiteSpace(item.StateBefore)
                    || !string.IsNullOrWhiteSpace(item.StateAfter));
            LSDeveloperTraceEvent taskEvidence = LatestReportEvent(events, focus,
                item => !string.IsNullOrWhiteSpace(item.TaskBefore)
                    || !string.IsNullOrWhiteSpace(item.TaskAfter));
            LSDeveloperTraceEvent physicalEvidence = LatestReportEvent(events, focus,
                item => !string.IsNullOrWhiteSpace(item.PhysicalBehavior)
                    || !string.IsNullOrWhiteSpace(item.TaskStatus)
                    || !float.IsNaN(item.MovementDelta)
                    || item.HasPosition);
            string runtimeSessionId = events.Select(item => item.RuntimeSessionId)
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? "Not recorded";

            _testResultSummaryItem.Title = "Test Result: " + HumanResultLabel(result.Classification);
            _testResultSummaryItem.Description = HumanResult(result.Classification)
                + (readableReportWritten
                    ? " A compact copy is saved to Plugin\\LSDeveloperReport.md."
                    : " The optional Plugin note was not written.");

            string[] values =
            {
                ReportText(result.Event, 100) + " / " + ReportText(result.Branch, 160),
                ReportText(result.TestId, 100),
                ReportText(runtimeSessionId, 100),
                ReportText(result.ExpectedBehavior, 360),
                ReportText(result.FlowConnection, 360),
                DescribeReportObservation(focus),
                DescribeReportEntity(focus == null ? 0 : focus.ActorHandle,
                    focus == null ? string.Empty : focus.ActorModel),
                DescribeReportOwner(focus),
                DescribeReportTarget(focus),
                DescribeReportTransition(stateEvidence, true),
                DescribeReportTransition(taskEvidence, false),
                DescribeReportPhysical(physicalEvidence),
                DescribeReportDivergence(result, focus),
                DescribeReportSource(result, focus),
                HumanResult(result.Classification)
            };
            for (int i = 0; i < _testResultFields.Count; i++)
                _testResultFields[i].Description = ReportText(values[i], 360);

            _testTimelineMenu.Clear();
            List<LSDeveloperTraceEvent> timeline = LastItems(events, 12);
            if (focus != null && !timeline.Contains(focus))
            {
                if (timeline.Count >= 12)
                    timeline[0] = focus;
                else
                    timeline.Add(focus);
            }
            timeline = timeline.OrderBy(item => item.Sequence).ToList();
            if (timeline.Count == 0)
                AddReadOnlyReportItem(_testTimelineMenu, "No captured events",
                    "No trace events were recorded for this test.");
            else
            {
                foreach (LSDeveloperTraceEvent trace in timeline)
                    AddReadOnlyReportItem(
                        _testTimelineMenu,
                        FormatTime(trace.TimestampUtc) + " — " + ReadableEventKind(trace.EventKind),
                        DescribeReadableTraceEvent(trace));
            }

            _testUnknownMenu.Clear();
            int unknownCount = 0;
            foreach (string unknown in result.Unknown.Take(7))
            {
                unknownCount++;
                AddReadOnlyReportItem(
                    _testUnknownMenu,
                    "Unknown " + unknownCount.ToString(CultureInfo.InvariantCulture),
                    ReportText(unknown, 360));
            }
            if (result.ExpectedStepCount == 0)
            {
                unknownCount++;
                AddReadOnlyReportItem(
                    _testUnknownMenu,
                    "Expected flow not mapped",
                    "No expected route is specified for this selection. Captured evidence remains reviewable, but no documented pass/failure comparison is available.");
            }
            if (unknownCount == 0)
                AddReadOnlyReportItem(_testUnknownMenu, "No extra unknowns recorded",
                    "This means the analyzer emitted no additional unknown notes; it is not proof every game-level cause is known.");
        }

        private void OpenResultView()
        {
            foreach (NativeMenu menu in Menus)
            {
                if (menu != null)
                    menu.Visible = false;
            }
            _testResultMenu.Visible = true;
        }

        private static void AddReadOnlyReportItem(NativeMenu menu, string title, string description)
        {
            NativeItem item = new NativeItem(ReportText(title, 100), ReportText(description, 420));
            item.Enabled = false;
            menu.Add(item);
        }

        private string AnalysisNotification(bool readableReportWritten)
        {
            return readableReportWritten
                ? "Test result is open. Readable note saved to Plugin\\LSDeveloperReport.md."
                : "Test result is open. Readable note was not saved; check Developer settings and LSDebug.log.";
        }

        private bool WriteHumanReadableReport(LSDeveloperAnalysisResult result)
        {
            if (!_settings.WriteHumanReadableReport
                || result == null
                || string.IsNullOrWhiteSpace(_paths.DeveloperReportPath))
                return false;

            List<LSDeveloperTraceEvent> events = result.Events ?? new List<LSDeveloperTraceEvent>();
            LSDeveloperTraceEvent focus = SelectReportEvent(result, events);
            LSDeveloperTraceEvent stateEvidence = LatestReportEvent(events, focus,
                item => !string.IsNullOrWhiteSpace(item.StateBefore)
                    || !string.IsNullOrWhiteSpace(item.StateAfter));
            LSDeveloperTraceEvent taskEvidence = LatestReportEvent(events, focus,
                item => !string.IsNullOrWhiteSpace(item.TaskBefore)
                    || !string.IsNullOrWhiteSpace(item.TaskAfter));
            LSDeveloperTraceEvent physicalEvidence = LatestReportEvent(events, focus,
                item => !string.IsNullOrWhiteSpace(item.PhysicalBehavior)
                    || !string.IsNullOrWhiteSpace(item.TaskStatus)
                    || !float.IsNaN(item.MovementDelta)
                    || item.HasPosition);
            string runtimeSessionId = events.Select(item => item.RuntimeSessionId)
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? "Not recorded";

            StringBuilder output = new StringBuilder();
            output.AppendLine("# LS Immersive Life — Developer Test Note");
            output.AppendLine();
            output.AppendLine("**Generated:** " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
            output.AppendLine("**Test ID:** " + ReportText(result.TestId, 100));
            output.AppendLine("**Runtime Session ID:** " + ReportText(runtimeSessionId, 100));
            output.AppendLine("**Test Event / Branch:** " + ReportText(result.Event, 100)
                + " / " + ReportText(result.Branch, 160));
            output.AppendLine();
            output.AppendLine("| Inspection field | Test result |");
            output.AppendLine("| --- | --- |");
            AddReportRow(output, "Expected Event", ReportText(result.ExpectedBehavior, 700));
            AddReportRow(output, "Flow Connection", ReportText(result.FlowConnection, 500));
            AddReportRow(output, "Observed Route", ReportText(result.SelectedRoute, 240));
            AddReportRow(output, "Observed Event", DescribeReportObservation(focus));
            AddReportRow(output, "Actor", DescribeReportEntity(
                focus == null ? 0 : focus.ActorHandle,
                focus == null ? string.Empty : focus.ActorModel));
            AddReportRow(output, "Owner System File", DescribeReportOwner(focus));
            AddReportRow(output, "Target", DescribeReportTarget(focus));
            AddReportRow(output, "State", DescribeReportTransition(stateEvidence, true));
            AddReportRow(output, "Task", DescribeReportTransition(taskEvidence, false));
            AddReportRow(output, "Physical Behavior", DescribeReportPhysical(physicalEvidence));
            AddReportRow(output, "First Divergence", DescribeReportDivergence(result, focus));
            AddReportRow(output, "Source", DescribeReportSource(result, focus));
            AddReportRow(output, "Result", HumanResult(result.Classification));
            output.AppendLine();
            output.AppendLine("**How to read this note:** It reports only evidence exposed by the current runtime. “Unknown” means the evidence was not available; it is not a guessed cause. Developer Mode observes gameplay and does not change it.");
            output.AppendLine();
            output.AppendLine("## Evidence timeline");
            output.AppendLine();
            output.AppendLine("Showing up to 20 recorded events. The first divergence is retained when one was directly observed.");
            List<LSDeveloperTraceEvent> timeline = LastItems(events, 20);
            if (focus != null && !timeline.Contains(focus))
            {
                if (timeline.Count >= 20)
                    timeline[0] = focus;
                else
                    timeline.Add(focus);
            }
            timeline = timeline.OrderBy(item => item.Sequence).ToList();
            if (timeline.Count == 0)
                output.AppendLine("- No trace events were captured for this test.");
            else
            {
                foreach (LSDeveloperTraceEvent trace in timeline)
                    output.AppendLine("- **" + FormatTime(trace.TimestampUtc) + "** — "
                        + ReportText(DescribeReadableTraceEvent(trace), 520));
            }

            output.AppendLine();
            output.AppendLine("## What remains unknown");
            output.AppendLine();
            if (result.Unknown.Count == 0)
                output.AppendLine("- No additional unknowns were emitted by the analyzer. This is not proof that every game-level cause is known.");
            else
            {
                foreach (string unknown in result.Unknown.Take(8))
                    output.AppendLine("- " + ReportText(unknown, 320));
                if (result.Unknown.Count > 8)
                    output.AppendLine("- Additional unknown notes were omitted to keep this report short.");
            }
            if (result.ExpectedStepCount == 0)
            {
                output.AppendLine("- No expected route is specified for this selection. The report can describe captured events and confirmed anomalies, but cannot compare a documented complete flow.");
            }

            try
            {
                string directory = Path.GetDirectoryName(_paths.DeveloperReportPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                // An explicit Analyze action replaces the previous note rather
                // than appending, so the human-readable file stays bounded.
                File.WriteAllText(_paths.DeveloperReportPath, output.ToString(), new UTF8Encoding(false));
                return true;
            }
            catch (Exception error)
            {
                LogDebug("DEVELOPER_HUMAN_REPORT_WRITE_FAILED", error.Message);
                return false;
            }
        }

        private static LSDeveloperTraceEvent SelectReportEvent(
            LSDeveloperAnalysisResult result,
            List<LSDeveloperTraceEvent> events)
        {
            if (result != null && result.FirstDivergenceEvent != null)
                return result.FirstDivergenceEvent;
            return events.LastOrDefault(item => item.EventKind != "ObservedState")
                ?? events.LastOrDefault();
        }

        private static LSDeveloperTraceEvent LatestReportEvent(
            List<LSDeveloperTraceEvent> events,
            LSDeveloperTraceEvent focus,
            Func<LSDeveloperTraceEvent, bool> predicate)
        {
            IEnumerable<LSDeveloperTraceEvent> candidates = events ?? new List<LSDeveloperTraceEvent>();
            if (focus != null)
            {
                if (focus.ActorHandle <= 0)
                    return predicate(focus) ? focus : null;
                candidates = candidates.Where(item => item.ActorHandle == focus.ActorHandle);
                if (focus.Sequence > 0)
                    candidates = candidates.Where(item => item.Sequence <= focus.Sequence);
            }
            return candidates.LastOrDefault(predicate);
        }

        private static void AddReportRow(StringBuilder output, string field, string value)
        {
            output.Append("| ").Append(field).Append(" | ")
                .Append(ReportCell(value)).AppendLine(" |");
        }

        private static string ReportCell(string value)
        {
            return ReportText(value, 700).Replace("|", "\\|");
        }

        private static string ReportText(string value, int maximumLength)
        {
            string normalized = (value ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();
            return string.IsNullOrWhiteSpace(normalized)
                ? "Not recorded"
                : Limit(normalized, maximumLength);
        }

        private static string HumanResultLabel(string classification)
        {
            if (string.Equals(classification, "PASSED", StringComparison.OrdinalIgnoreCase)
                || string.Equals(classification, "PASS", StringComparison.OrdinalIgnoreCase))
                return "Passed";
            if (string.Equals(classification, "CONFIRMED_FAILURE_EVIDENCE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(classification, "CONFIRMED_ANOMALY", StringComparison.OrdinalIgnoreCase))
                return "Failed";
            if (string.Equals(classification, "INCOMPLETE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(classification, "OBSERVED_INCOMPLETE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(classification, "NO_DIVERGENCE_OBSERVED", StringComparison.OrdinalIgnoreCase))
                return "Incomplete";
            return "Unknown";
        }

        private static string HumanResult(string classification)
        {
            string label = HumanResultLabel(classification);
            switch (label)
            {
                case "Passed":
                    return "Passed — the mapped checks were satisfied; verify the required physical terminal outcome in-game.";
                case "Failed":
                    return "Failed — a failure or anomaly was directly observed; its hidden cause may remain unknown.";
                case "Incomplete":
                    return "Incomplete — the captured evidence does not prove the full expected physical outcome.";
                default:
                    return "Unknown — the available evidence is not enough to classify the behavior.";
            }
        }

        private static string DescribeReportObservation(LSDeveloperTraceEvent trace)
        {
            if (trace == null)
                return "No direct divergence event was recorded.";
            string description = ReadableEventKind(trace.EventKind);
            if (!string.IsNullOrWhiteSpace(trace.Reason))
                description += ": " + trace.Reason;
            if (!string.IsNullOrWhiteSpace(trace.ExpectedNext))
                description += " Expected next: " + trace.ExpectedNext + ".";
            return ReportText(description, 700);
        }

        private static string DescribeReportEntity(int handle, string model)
        {
            if (handle <= 0)
                return "No actor handle was recorded.";
            return "Entity handle " + handle.ToString(CultureInfo.InvariantCulture)
                + (string.IsNullOrWhiteSpace(model)
                    ? "; model hash not recorded; entity type not exposed."
                    : "; model hash " + ReportText(model, 80) + "; entity type not exposed.");
        }

        private static string DescribeReportOwner(LSDeveloperTraceEvent trace)
        {
            if (trace == null)
                return "Owner and source file were not exposed in the captured evidence.";
            string owner = ValueOrUnknown(trace.Owner);
            string file = string.IsNullOrWhiteSpace(trace.SourceFile)
                ? "source file not recorded"
                : trace.SourceFile;
            string member = string.IsNullOrWhiteSpace(trace.SourceMember)
                ? string.Empty
                : " / " + trace.SourceMember;
            return owner + " — " + file + member;
        }

        private static string DescribeReportTarget(LSDeveloperTraceEvent trace)
        {
            if (trace == null)
                return "No target was recorded.";
            string target = trace.TargetHandle <= 0
                ? "Target handle not recorded"
                : "Entity handle " + trace.TargetHandle.ToString(CultureInfo.InvariantCulture)
                    + (string.IsNullOrWhiteSpace(trace.TargetModel)
                        ? "; model hash not recorded"
                        : "; model hash " + ReportText(trace.TargetModel, 80));
            if (trace.VehicleHandle > 0)
                target += "; related vehicle handle " + trace.VehicleHandle.ToString(CultureInfo.InvariantCulture)
                    + (string.IsNullOrWhiteSpace(trace.VehicleModel)
                        ? " (model hash not recorded)"
                        : " (model hash " + ReportText(trace.VehicleModel, 80) + ")");
            return target + ".";
        }

        private static string DescribeReportTransition(LSDeveloperTraceEvent trace, bool state)
        {
            if (trace == null)
                return state ? "Logical state was not recorded." : "Task was not recorded.";
            string before = state ? trace.StateBefore : trace.TaskBefore;
            string after = state ? trace.StateAfter : trace.TaskAfter;
            if (!string.IsNullOrWhiteSpace(before) && !string.IsNullOrWhiteSpace(after))
                return ReportText(before, 180) + " → " + ReportText(after, 180);
            string value = string.IsNullOrWhiteSpace(after) ? before : after;
            return string.IsNullOrWhiteSpace(value)
                ? (state ? "Logical state was not recorded." : "Task was not recorded.")
                : "Latest recorded value: " + ReportText(value, 360);
        }

        private static string DescribeReportPhysical(LSDeveloperTraceEvent trace)
        {
            if (trace == null)
                return "No physical sample was attached to the relevant event.";
            List<string> evidence = new List<string>();
            if (!string.IsNullOrWhiteSpace(trace.PhysicalBehavior))
                evidence.Add(trace.PhysicalBehavior);
            if (!string.IsNullOrWhiteSpace(trace.TaskStatus))
                evidence.Add("GTA task status: " + trace.TaskStatus);
            if (!float.IsNaN(trace.TargetDistance))
                evidence.Add("target distance " + trace.TargetDistance.ToString("0.0", CultureInfo.InvariantCulture) + " m");
            if (!float.IsNaN(trace.MovementDelta))
                evidence.Add("sampled movement " + trace.MovementDelta.ToString("0.0", CultureInfo.InvariantCulture) + " m");
            if (trace.HasPosition)
                evidence.Add("position " + trace.Position);
            if (evidence.Count == 0 && !string.IsNullOrWhiteSpace(trace.Reason))
                evidence.Add(trace.Reason);
            return evidence.Count == 0
                ? "No physical behavior was directly recorded for the relevant event."
                : string.Join("; ", evidence.ToArray());
        }

        private static string DescribeReportDivergence(
            LSDeveloperAnalysisResult result,
            LSDeveloperTraceEvent focus)
        {
            if (result == null)
                return "Analysis result unavailable.";
            if (focus != null)
            {
                if (focus.EventKind == "BehaviorStall")
                    return "The expected continuation did not begin during the observed stall window. The trace does not prove GTA's hidden reason for not continuing.";
                if (focus.EventKind == "PhysicalMismatch")
                    return "The sampled physical behavior did not match the reported logical state: "
                        + ReportText(focus.StateAfter, 140) + " / " + ReportText(focus.PhysicalBehavior, 260) + ".";
                if (focus.EventKind == "TaskConflict")
                    return "Two task owners issued a conflict for the same actor. Developer Mode recorded the conflict and did not resolve it.";
                return ReadableEventKind(focus.EventKind)
                    + (string.IsNullOrWhiteSpace(focus.Reason) ? " was recorded." : ": " + focus.Reason);
            }
            if (result.ExpectedStepCount == 0)
                return "No expected route is specified for this selection, so there is no documented step to compare.";
            if (string.Equals(result.Classification, "NO_DIVERGENCE_OBSERVED", StringComparison.OrdinalIgnoreCase))
                return "No divergence was observed in the captured evidence; this is not a pass unless the expected flow and physical completion were confirmed.";
            return ReportText(result.FirstDivergence, 600);
        }

        private static string DescribeReportSource(
            LSDeveloperAnalysisResult result,
            LSDeveloperTraceEvent trace)
        {
            string document = result == null ? string.Empty : result.BranchSource;
            string runtime = trace == null || string.IsNullOrWhiteSpace(trace.SourceFile)
                ? "runtime source file/member not recorded"
                : trace.SourceFile + (string.IsNullOrWhiteSpace(trace.SourceMember)
                    ? string.Empty : " / " + trace.SourceMember);
            return "Gameplay Branch: " + ReportText(document, 250)
                + "; runtime evidence: " + runtime + ".";
        }

        private static string DescribeReadableTraceEvent(LSDeveloperTraceEvent trace)
        {
            if (trace == null)
                return "Unknown event.";
            StringBuilder description = new StringBuilder(ReadableEventKind(trace.EventKind));
            if (!string.IsNullOrWhiteSpace(trace.Owner))
                description.Append("; owner ").Append(trace.Owner);
            if (trace.ActorHandle > 0)
                description.Append("; actor ").Append(trace.ActorHandle.ToString(CultureInfo.InvariantCulture));
            if (trace.TargetHandle > 0)
                description.Append("; target ").Append(trace.TargetHandle.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(trace.StateAfter))
                description.Append("; state ").Append(trace.StateAfter);
            if (!string.IsNullOrWhiteSpace(trace.TaskAfter))
                description.Append("; task ").Append(trace.TaskAfter);
            if (!string.IsNullOrWhiteSpace(trace.PhysicalBehavior))
                description.Append("; observed behavior ").Append(trace.PhysicalBehavior);
            if (!string.IsNullOrWhiteSpace(trace.Reason))
                description.Append("; reason ").Append(trace.Reason);
            if (!string.IsNullOrWhiteSpace(trace.ExpectedNext))
                description.Append("; expected next ").Append(trace.ExpectedNext);
            if (!string.IsNullOrWhiteSpace(trace.SourceFile))
                description.Append("; source ").Append(trace.SourceFile)
                    .Append(string.IsNullOrWhiteSpace(trace.SourceMember) ? string.Empty : " / " + trace.SourceMember);
            if (!string.IsNullOrWhiteSpace(trace.TaskStatus))
                description.Append("; task status ").Append(trace.TaskStatus);
            if (!float.IsNaN(trace.TargetDistance))
                description.Append("; target distance ").Append(trace.TargetDistance.ToString("0.0", CultureInfo.InvariantCulture)).Append(" m");
            if (!float.IsNaN(trace.MovementDelta))
                description.Append("; movement ").Append(trace.MovementDelta.ToString("0.0", CultureInfo.InvariantCulture)).Append(" m");
            return ReportText(description.ToString(), 520);
        }

        private static string ReadableEventKind(string eventKind)
        {
            if (string.Equals(eventKind, "BehaviorStall", StringComparison.OrdinalIgnoreCase))
                return "Behavioral stall observed";
            if (string.Equals(eventKind, "PhysicalMismatch", StringComparison.OrdinalIgnoreCase))
                return "Logical/physical mismatch observed";
            if (string.Equals(eventKind, "TaskConflict", StringComparison.OrdinalIgnoreCase))
                return "Task ownership conflict observed";
            if (string.Equals(eventKind, "ObservedState", StringComparison.OrdinalIgnoreCase))
                return "Live actor state sampled";
            if (string.IsNullOrWhiteSpace(eventKind))
                return "Unclassified event";
            StringBuilder readable = new StringBuilder();
            for (int i = 0; i < eventKind.Length; i++)
            {
                char current = eventKind[i];
                if (current == '_')
                    readable.Append(' ');
                else
                {
                    if (i > 0 && char.IsUpper(current) && eventKind[i - 1] != '_' && char.IsLower(eventKind[i - 1]))
                        readable.Append(' ');
                    readable.Append(current);
                }
            }
            return readable.ToString();
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

        private NativeItem AddAction(string title, string description, Action action)
        {
            NativeItem item = new NativeItem(title, description);
            item.Activated += delegate
            {
                if (_settings.Enabled && _settings.DeveloperMenuEnabled && action != null)
                {
                    action();
                    RefreshMenuControls();
                }
                else
                    Notify("Developer Mode is disabled in LSDeveloper.xml.");
            };
            Menu.Add(item);
            return item;
        }

        private void Notify(string message)
        {
            if (!_settings.ShowLiveNotifications)
                return;
            Notification.PostTicker(FormatNotification(message), false, false);
        }

        private static string FormatNotification(string message)
        {
            string normalized = (message ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Replace("~n~", "\n")
                .Replace(" || ", "\n")
                .Replace("; ", "\n");
            string[] sections = normalized.Split(
                new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var lines = new List<string>();
            bool truncated = false;

            foreach (string section in sections)
            {
                string remaining = section.Trim();
                while (remaining.Length > NotificationLineCharacterLimit)
                {
                    if (lines.Count >= NotificationLineLimit)
                    {
                        truncated = true;
                        break;
                    }

                    int splitAt = remaining.LastIndexOf(
                        ' ', NotificationLineCharacterLimit - 1,
                        NotificationLineCharacterLimit);
                    if (splitAt <= 0)
                        splitAt = NotificationLineCharacterLimit;
                    lines.Add(remaining.Substring(0, splitAt).Trim());
                    remaining = remaining.Substring(splitAt).Trim();
                }

                if (truncated)
                    break;
                if (remaining.Length > 0)
                {
                    if (lines.Count >= NotificationLineLimit)
                    {
                        truncated = true;
                        break;
                    }
                    lines.Add(remaining);
                }
            }

            if (lines.Count == 0)
                return "Developer notification.";
            if (truncated)
            {
                const string detailsPrompt = "Open Developer for full details.";
                if (lines.Count >= NotificationLineLimit)
                    lines[NotificationLineLimit - 1] = detailsPrompt;
                else
                    lines.Add(detailsPrompt);
            }
            return string.Join("~n~", lines.ToArray());
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
                || value.Contains("AUTHORITY")
                || value.Contains("PROFILE")
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

            if (value == "POLICE_GROUP_SUSPECT_SECURED")
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

    internal sealed class LSDeveloperDispatchSceneUpdate
    {
        internal string Stage { get; set; }
        internal DateTime TimestampUtc { get; set; }
        internal string SearchId { get; set; }
        internal string OccurrenceId { get; set; }
        internal int CandidateAttempt { get; set; }
        internal string EventId { get; set; }
        internal string EventTitle { get; set; }
        internal string LocationId { get; set; }
        internal string LocationName { get; set; }
        internal string LocationCategory { get; set; }
        internal string PointId { get; set; }
        internal Vector3 Position { get; set; }
        internal bool IsInterior { get; set; }
        internal bool RequiresVehicle { get; set; }
        internal string CriminalProfileId { get; set; }
        internal string CriminalModel { get; set; }
        internal string VictimPoolId { get; set; }
        internal string SceneParticipantPoolId { get; set; }
        internal int RequiredActorCount { get; set; }
        internal int ActorsCreated { get; set; }
        internal int ActorsReused { get; set; }
        internal List<int> ActorHandles { get; set; } = new List<int>();
        internal List<string> ActorRoles { get; set; } = new List<string>();
        internal List<string> BranchIds { get; set; } = new List<string>();
        internal List<string> SuspectResponseBranches { get; set; } = new List<string>();
        internal List<string> VictimResponseBranches { get; set; } = new List<string>();
        internal string PreArrivalActivity { get; set; }
        internal string FailureStage { get; set; }
        internal string Reason { get; set; }

        internal LSDeveloperDispatchSceneUpdate Clone()
        {
            return new LSDeveloperDispatchSceneUpdate
            {
                Stage = Stage,
                TimestampUtc = TimestampUtc,
                SearchId = SearchId,
                OccurrenceId = OccurrenceId,
                CandidateAttempt = CandidateAttempt,
                EventId = EventId,
                EventTitle = EventTitle,
                LocationId = LocationId,
                LocationName = LocationName,
                LocationCategory = LocationCategory,
                PointId = PointId,
                Position = Position,
                IsInterior = IsInterior,
                RequiresVehicle = RequiresVehicle,
                CriminalProfileId = CriminalProfileId,
                CriminalModel = CriminalModel,
                VictimPoolId = VictimPoolId,
                SceneParticipantPoolId = SceneParticipantPoolId,
                RequiredActorCount = RequiredActorCount,
                ActorsCreated = ActorsCreated,
                ActorsReused = ActorsReused,
                ActorHandles = new List<int>(ActorHandles ?? new List<int>()),
                ActorRoles = new List<string>(ActorRoles ?? new List<string>()),
                BranchIds = new List<string>(BranchIds ?? new List<string>()),
                SuspectResponseBranches = new List<string>(
                    SuspectResponseBranches ?? new List<string>()),
                VictimResponseBranches = new List<string>(
                    VictimResponseBranches ?? new List<string>()),
                PreArrivalActivity = PreArrivalActivity,
                FailureStage = FailureStage,
                Reason = Reason
            };
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

        internal static bool DispatchSceneInspectionEnabled
        {
            get
            {
                LSImmersiveDeveloper developer;
                lock (Sync)
                    developer = _current;
                return developer != null && developer.IsDispatchSceneInspectionEnabled;
            }
        }

        internal static void ReportDispatchSceneUpdate(
            LSDeveloperDispatchSceneUpdate update)
        {
            LSImmersiveDeveloper developer;
            lock (Sync)
                developer = _current;
            if (developer == null)
                return;
            try { developer.ObserveDispatchSceneUpdate(update); }
            catch
            {
                // The diagnostic inspector must never interrupt a gameplay owner.
            }
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
        internal bool CheckDispatchScenes { get; set; }
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
        internal bool WriteHumanReadableReport { get; set; }

        private LSDeveloperSettings()
        {
            Enabled = true;
            DeveloperMenuEnabled = true;
            CheckDispatchScenes = false;
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
            WriteHumanReadableReport = true;
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
                result.CheckDispatchScenes = Bool(
                    root, "checkDispatchScenes", result.CheckDispatchScenes);
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
                result.WriteHumanReadableReport = Bool(root, "writeHumanReadableReport", result.WriteHumanReadableReport);
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
            CheckDispatchScenes = source.CheckDispatchScenes;
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
            WriteHumanReadableReport = source.WriteHumanReadableReport;
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
                    new XAttribute("checkDispatchScenes", CheckDispatchScenes),
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
                    new XAttribute("writeTraceFile", WriteTraceFile),
                    new XAttribute("writeHumanReadableReport", WriteHumanReadableReport)));
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
                ActorHandle = Handle(fields, "Actor", "Ped", "Driver", "Subject", "Prisoner", "Officer", "Suspect", "Handle"),
                ActorModel = Value(fields, "ActorModel", "PedModel", "Model", "PedAsset"),
                TargetHandle = Handle(fields, "Target", "TargetPed", "Suspect"),
                TargetModel = Value(fields, "TargetModel", "TargetAsset"),
                VehicleHandle = Handle(fields, "Vehicle", "SubjectVehicle", "BackupVehicle", "Transport"),
                VehicleModel = Value(fields, "VehicleModel", "VehicleAsset"),
                StateBefore = Value(fields, "StateBefore", "PreviousState", "BeforeState"),
                StateAfter = Value(fields, "StateAfter", "State", "Stage", "Phase"),
                TaskBefore = Value(fields, "TaskBefore", "PreviousTask", "BeforeTask"),
                TaskAfter = Value(fields, "TaskAfter", "Task", "TaskName", "Action"),
                Reason = Value(fields, "Reason", "FailureReason", "Failure", "Result"),
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
            if (value.Contains("INTERIOR_ACTOR_PREFLIGHT")
                && (message ?? string.Empty).IndexOf(
                    "Result=Rejected", StringComparison.OrdinalIgnoreCase) >= 0)
                return "PhysicalMismatch";
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
        internal string[] Categories { get; private set; }

        internal LSDeveloperExpectedStep(string name, IEnumerable<string> categories)
        {
            Name = name;
            Categories = (categories ?? Enumerable.Empty<string>()).ToArray();
        }

        internal bool Matches(LSDeveloperTraceEvent trace)
        {
            return trace != null && Categories.Any(category =>
                string.Equals(trace.Category, category, StringComparison.OrdinalIgnoreCase));
        }
    }

    internal sealed class LSDeveloperExpectedRoute
    {
        internal string Name { get; private set; }
        internal string[] SelectorCategories { get; private set; }
        internal List<LSDeveloperExpectedStep> Steps { get; private set; }

        internal LSDeveloperExpectedRoute(
            string name,
            IEnumerable<string> selectorCategories,
            IEnumerable<LSDeveloperExpectedStep> steps)
        {
            Name = name;
            SelectorCategories = (selectorCategories ?? Enumerable.Empty<string>()).ToArray();
            Steps = new List<LSDeveloperExpectedStep>(steps ?? Enumerable.Empty<LSDeveloperExpectedStep>());
        }
    }

    internal sealed class LSDeveloperBranchDefinition
    {
        internal string Id { get; private set; }
        internal string EventId { get; private set; }
        internal string EventName { get; private set; }
        internal string Name { get; private set; }
        internal string SourceSection { get; private set; }
        internal string SourceLines { get; private set; }
        internal string ExpectedBehavior { get; private set; }
        internal string FlowConnection { get; private set; }
        internal List<LSDeveloperExpectedStep> Steps { get; private set; }
        internal List<LSDeveloperExpectedRoute> Routes { get; private set; }

        internal LSDeveloperBranchDefinition(
            string id,
            string eventId,
            string eventName,
            string name,
            string sourceSection,
            string sourceLines,
            string expectedBehavior,
            string flowConnection,
            IEnumerable<LSDeveloperExpectedStep> steps,
            IEnumerable<LSDeveloperExpectedRoute> routes)
        {
            Id = id;
            EventId = eventId;
            EventName = eventName;
            Name = name;
            SourceSection = sourceSection;
            SourceLines = sourceLines;
            ExpectedBehavior = expectedBehavior;
            FlowConnection = flowConnection;
            Steps = new List<LSDeveloperExpectedStep>(steps ?? Enumerable.Empty<LSDeveloperExpectedStep>());
            Routes = new List<LSDeveloperExpectedRoute>(routes ?? Enumerable.Empty<LSDeveloperExpectedRoute>());
        }
    }

    internal sealed class LSDeveloperTestEventDefinition
    {
        internal string Id { get; private set; }
        internal string Name { get; private set; }
        internal string SourceSection { get; private set; }
        internal string SourceLines { get; private set; }
        internal List<LSDeveloperBranchDefinition> Branches { get; private set; }

        internal LSDeveloperTestEventDefinition(
            string id,
            string name,
            string sourceSection,
            string sourceLines,
            IEnumerable<LSDeveloperBranchDefinition> branches)
        {
            Id = id;
            Name = name;
            SourceSection = sourceSection;
            SourceLines = sourceLines;
            Branches = new List<LSDeveloperBranchDefinition>(branches ?? Enumerable.Empty<LSDeveloperBranchDefinition>());
        }
    }

    internal sealed class LSDeveloperBranchCatalog
    {
        private readonly List<LSDeveloperTestEventDefinition> _events;

        private LSDeveloperBranchCatalog(IEnumerable<LSDeveloperTestEventDefinition> events)
        {
            _events = new List<LSDeveloperTestEventDefinition>(events ?? Enumerable.Empty<LSDeveloperTestEventDefinition>());
        }

        internal static LSDeveloperBranchCatalog Load(string path, Action<string> diagnostic)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    throw new FileNotFoundException("The documented Developer Branch XML was not found.", path);

                XmlReaderSettings settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };

                XDocument document;
                using (XmlReader reader = XmlReader.Create(path, settings))
                    document = XDocument.Load(reader);

                XElement root = document.Root;
                if (root == null
                    || root.Name != "LSDeveloperBranches"
                    || Attribute(root, "version") != "1")
                    throw new InvalidDataException("Unexpected LSDeveloperBranches XML root or version.");

                List<LSDeveloperTestEventDefinition> events = new List<LSDeveloperTestEventDefinition>();
                foreach (XElement eventElement in root.Elements("TestEvent"))
                {
                    string eventId = Attribute(eventElement, "id");
                    string eventName = Attribute(eventElement, "name");
                    if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(eventName))
                        throw new InvalidDataException("Each Developer TestEvent requires an id and name.");

                    List<LSDeveloperExpectedStep> commonSteps = ReadSteps(eventElement.Element("CommonSteps"));
                    if (commonSteps.Count > 20)
                        throw new InvalidDataException("A Developer TestEvent may define at most 20 common expected steps.");

                    List<LSDeveloperBranchDefinition> branches = new List<LSDeveloperBranchDefinition>();
                    foreach (XElement branchElement in eventElement.Elements("Branch"))
                    {
                        string branchId = Attribute(branchElement, "id");
                        string branchName = Attribute(branchElement, "name");
                        if (string.IsNullOrWhiteSpace(branchId) || string.IsNullOrWhiteSpace(branchName))
                            throw new InvalidDataException("Each Developer Branch requires an id and name.");
                        string flowConnection = string.IsNullOrWhiteSpace(Attribute(branchElement, "flowConnection"))
                            ? Attribute(eventElement, "flowConnection")
                            : Attribute(branchElement, "flowConnection");
                        if (flowConnection.Length > 360)
                            throw new InvalidDataException("A Developer flow connection may not exceed 360 characters.");

                        bool skipCommonSteps = string.Equals(
                            Attribute(branchElement, "skipCommonSteps"),
                            "true",
                            StringComparison.OrdinalIgnoreCase);
                        IEnumerable<LSDeveloperExpectedStep> branchCommonSteps = skipCommonSteps
                            ? Enumerable.Empty<LSDeveloperExpectedStep>()
                            : commonSteps;
                        List<LSDeveloperExpectedStep> steps = new List<LSDeveloperExpectedStep>();
                        List<LSDeveloperExpectedRoute> routes = new List<LSDeveloperExpectedRoute>();
                        XElement stepsElement = branchElement.Element("ExpectedSteps");
                        XElement routesElement = branchElement.Element("ExpectedRoutes");
                        if (stepsElement != null && routesElement != null)
                            throw new InvalidDataException("A Developer Branch may define ExpectedSteps or ExpectedRoutes, not both.");

                        if (stepsElement != null)
                        {
                            steps = branchCommonSteps.Concat(ReadSteps(stepsElement)).ToList();
                            if (steps.Count > 80)
                                throw new InvalidDataException("A Developer Branch may define at most 80 expected steps.");
                        }

                        if (routesElement != null)
                        {
                            XElement[] routeElements = routesElement.Elements("Route").ToArray();
                            if (routeElements.Length == 0 || routeElements.Length > 12)
                                throw new InvalidDataException("ExpectedRoutes must contain between 1 and 12 routes.");

                            foreach (XElement routeElement in routeElements)
                            {
                                string routeName = Attribute(routeElement, "name");
                                string[] selectorCategories = ReadCategories(routeElement, "selectOn");
                                List<LSDeveloperExpectedStep> routeSteps = branchCommonSteps
                                    .Concat(ReadSteps(routeElement))
                                    .ToList();
                                if (string.IsNullOrWhiteSpace(routeName) || routeSteps.Count == 0 || routeSteps.Count > 80)
                                    throw new InvalidDataException("Each Developer expected route requires a name and between 1 and 80 steps.");

                                routes.Add(new LSDeveloperExpectedRoute(routeName, selectorCategories, routeSteps));
                            }

                            if (routes.Select(route => route.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != routes.Count)
                                throw new InvalidDataException("Expected route names must be unique inside each Developer Branch.");

                            if (routes.Count > 1 && routes.Any(route => route.SelectorCategories.Length == 0))
                                throw new InvalidDataException("Each alternative route requires a selectOn trace category.");

                            string duplicateSelector = routes
                                .SelectMany(route => route.SelectorCategories.Select(category => new { route.Name, Category = category }))
                                .GroupBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
                                .Where(group => group.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                                .Select(group => group.Key)
                                .FirstOrDefault();
                            if (duplicateSelector != null)
                                throw new InvalidDataException("An alternative route selector cannot identify multiple routes: " + duplicateSelector);
                        }

                        branches.Add(new LSDeveloperBranchDefinition(
                            branchId,
                            eventId,
                            eventName,
                            branchName,
                            Attribute(branchElement, "sourceSection"),
                            Attribute(branchElement, "sourceLines"),
                            Attribute(branchElement, "expectedBehavior"),
                            flowConnection,
                            steps,
                            routes));
                    }

                    events.Add(new LSDeveloperTestEventDefinition(
                        eventId,
                        eventName,
                        Attribute(eventElement, "sourceSection"),
                        Attribute(eventElement, "sourceLines"),
                        branches));
                }

                if (events.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != events.Count
                    || events.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != events.Count)
                    throw new InvalidDataException("Developer TestEvent ids and names must be unique.");

                foreach (LSDeveloperTestEventDefinition testEvent in events)
                {
                    if (testEvent.Branches.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != testEvent.Branches.Count
                        || testEvent.Branches.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != testEvent.Branches.Count)
                        throw new InvalidDataException("Developer Branch ids and names must be unique inside each TestEvent.");
                }

                return new LSDeveloperBranchCatalog(events);
            }
            catch (Exception error)
            {
                if (diagnostic != null)
                    diagnostic("Could not load documented Developer branches from " + path + ": " + error.Message);
                return new LSDeveloperBranchCatalog(Enumerable.Empty<LSDeveloperTestEventDefinition>());
            }
        }

        internal string[] EventNames()
        {
            return _events.Select(item => item.Name).ToArray();
        }

        internal string[] BranchNames(string eventName)
        {
            LSDeveloperTestEventDefinition testEvent = FindEvent(eventName);
            return testEvent == null
                ? new string[0]
                : testEvent.Branches.Select(item => item.Name).ToArray();
        }

        internal LSDeveloperBranchDefinition Find(string eventName, string branchName)
        {
            LSDeveloperTestEventDefinition testEvent = FindEvent(eventName);
            return testEvent == null
                ? null
                : testEvent.Branches.FirstOrDefault(item => string.Equals(
                    item.Name,
                    branchName,
                    StringComparison.OrdinalIgnoreCase));
        }

        private LSDeveloperTestEventDefinition FindEvent(string eventName)
        {
            return _events.FirstOrDefault(item => string.Equals(
                item.Name,
                eventName,
                StringComparison.OrdinalIgnoreCase));
        }

        private static string Attribute(XElement element, string name)
        {
            XAttribute attribute = element == null ? null : element.Attribute(name);
            return attribute == null ? string.Empty : attribute.Value;
        }

        private static List<LSDeveloperExpectedStep> ReadSteps(XElement parent)
        {
            List<LSDeveloperExpectedStep> steps = new List<LSDeveloperExpectedStep>();
            if (parent == null)
                return steps;

            foreach (XElement stepElement in parent.Elements("Step"))
            {
                string stepName = Attribute(stepElement, "name");
                string[] categories = ReadCategories(stepElement, "categories");
                if (string.IsNullOrWhiteSpace(stepName) || categories.Length == 0)
                    throw new InvalidDataException("Each Developer expected step requires a name and trace categories.");
                steps.Add(new LSDeveloperExpectedStep(stepName, categories));
            }
            return steps;
        }

        private static string[] ReadCategories(XElement element, string attributeName)
        {
            return Attribute(element, attributeName)
                .Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    internal sealed class LSDeveloperAnalysisResult
    {
        internal string TestId { get; set; }
        internal string Event { get; set; }
        internal string Branch { get; set; }
        internal string ExpectedBehavior { get; set; }
        internal string FlowConnection { get; set; }
        internal string SelectedRoute { get; set; }
        internal string BranchSource { get; set; }
        internal int ExpectedStepCount { get; set; }
        internal string Classification { get; set; }
        internal string FirstDivergence { get; set; }
        internal LSDeveloperTraceEvent FirstDivergenceEvent { get; set; }
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
