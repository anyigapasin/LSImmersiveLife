using System;
using LemonUI.Elements;
using System.Drawing;
using GTA;
using GTA.Math;
using GTA.Native;
using LemonUI.Tools;

namespace LSImmersiveLife
{
    internal sealed class LSPDStation
    {
        private const float StationEntryRadius = 1.8f;
        private const float StationMarkerRange = 90f;
        private const int PreviewFadeMilliseconds = 300;
        private const float AlmanacLeft = 0.035f;
        private const float AlmanacTop = 0.245f;
        private const float AlmanacWidth = 0.76f;
        private const float AlmanacHeight = 0.525f;

        private readonly ScaledRectangle _background;
        private readonly ScaledRectangle _accent;
        private readonly ScaledRectangle _bottomAccent;
        private readonly ScaledRectangle _leftAccent;
        private readonly ScaledRectangle _rightAccent;
        private readonly ScaledRectangle _headerDivider;
        private readonly ScaledRectangle _catalogBackground;
        private readonly ScaledRectangle _statusBackground;
        private readonly ScaledRectangle _operationsBackground;
        private readonly ScaledRectangle _catalogAccent;
        private readonly ScaledRectangle _statusAccent;
        private readonly ScaledRectangle _operationsAccent;
        private readonly ScaledRectangle _playerEntryBackground;
        private readonly ScaledRectangle _backupEntryBackground;
        private readonly ScaledRectangle _playerPortraitBackground;
        private readonly ScaledRectangle _backupPortraitBackground;
        private readonly ScaledText _playerPortraitFallback;
        private readonly ScaledText _backupPortraitFallback;
        private readonly ScaledRectangle _citizenStatusDot;
        private readonly ScaledRectangle _gangStatusDot;
        private readonly ScaledRectangle _crimeStatusDot;
        private readonly ScaledRectangle[] _statusRowBackgrounds = new ScaledRectangle[5];
        private readonly ScaledRectangle[] _statusDots = new ScaledRectangle[5];
        private readonly ScaledText[] _statusLabels = new ScaledText[5];
        private readonly ScaledText[] _statusValues = new ScaledText[5];
        private readonly ScaledText _title;
        private readonly ScaledText _subtitle;
        private readonly ScaledText _agencyHeader;
        private readonly ScaledText _agencyDetails;
        private readonly ScaledText _catalogHeader;
        private readonly ScaledText _catalogSubtitle;
        private readonly ScaledText _statusHeader;
        private readonly ScaledText _statusSubtitle;
        private readonly ScaledText _operationsHeader;
        private readonly ScaledText _operationsSubtitle;
        private readonly ScaledText _operationsWatermark;
        private readonly ScaledText _operationsWatermarkSubtitle;
        private readonly ScaledText _departmentDetails;
        private readonly ScaledText _playerDetails;
        private readonly ScaledText _backupDetails;
        private readonly ScaledText _citizenStatusDetails;
        private readonly ScaledText _gangStatusDetails;
        private readonly ScaledText _crimeStatusDetails;
        private int _playerHeadshotHandle;
        private int _playerHeadshotPedHandle;
        private int _playerHeadshotModelHash;
        private DateTime _playerHeadshotRetryAtUtc;
        private int _backupHeadshotHandle;
        private int _backupHeadshotPedHandle;
        private int _backupHeadshotModelHash;
        private DateTime _backupHeadshotRetryAtUtc;
        private Camera _previewCamera;
        private Camera _previousRenderingCamera;
        private bool _previewFocusActive;
        private Ped _previewPed;
        private Vehicle _previewVehicle;
        private bool _previewSessionActive;
        private bool _previewModeReady;
        private bool _previewTransitionWaiting;
        private bool _previewEnding;
        private DateTime _previewTransitionAtUtc;
        private StationPreviewMode _previewMode;
        private StationPreviewMode _requestedPreviewMode;
        private string _requestedPedModel;
        private string _requestedVehicleModel;
        private string _previewPedModel;
        private string _previewVehicleModel;

        internal LSPDStation()
        {
            _background = new ScaledRectangle(
                ScaledPoint(AlmanacLeft, AlmanacTop),
                ScaledSize(AlmanacWidth, AlmanacHeight));
            _background.Color = Color.FromArgb(225, 8, 18, 30);

            _accent = new ScaledRectangle(
                ScaledPoint(AlmanacLeft, AlmanacTop),
                ScaledSize(AlmanacWidth, 0.0045f));
            _accent.Color = Color.FromArgb(255, 35, 166, 231);
            _bottomAccent = CreateAccentBar(
                AlmanacLeft, AlmanacTop + AlmanacHeight - 0.003f, AlmanacWidth, 0.003f);
            _leftAccent = CreateAccentBar(AlmanacLeft, AlmanacTop, 0.002f, AlmanacHeight);
            _rightAccent = CreateAccentBar(
                AlmanacLeft + AlmanacWidth - 0.002f, AlmanacTop, 0.002f, AlmanacHeight);
            _headerDivider = CreateAccentBar(
                AlmanacLeft + 0.014f, AlmanacTop + 0.076f, AlmanacWidth - 0.028f, 0.0018f);

            _title = new ScaledText(
                ScaledPoint(AlmanacLeft + 0.026f, AlmanacTop + 0.010f),
                "ACTIVE OPERATIONS",
                0.48f,
                GTA.UI.Font.ChaletLondon);
            _title.Color = Color.White;
            _title.Shadow = true;

            _subtitle = CreateHeading(AlmanacLeft + 0.028f, AlmanacTop + 0.045f,
                "FIELD AREA", 0.255f);
            _agencyHeader = CreateRightAlignedText(
                AlmanacLeft + AlmanacWidth - 0.026f, AlmanacTop + 0.012f,
                "POLICE ALMANAC", 0.44f, Color.White);
            _agencyDetails = CreateRightAlignedText(
                AlmanacLeft + AlmanacWidth - 0.026f, AlmanacTop + 0.047f,
                string.Empty, 0.255f, Color.FromArgb(255, 88, 191, 227));

            float catalogX = AlmanacLeft + 0.014f;
            float catalogWidth = 0.360f;
            float statusWidth = 0.360f;
            float statusX = catalogX + catalogWidth + 0.012f;
            float sectionY = AlmanacTop + 0.086f;
            float sectionHeight = 0.234f;
            float operationsX = catalogX;
            float operationsY = AlmanacTop + 0.331f;
            float operationsWidth = AlmanacWidth - 0.028f;
            float operationsHeight = 0.160f;

            _catalogBackground = CreateSection(catalogX, sectionY, catalogWidth, sectionHeight);
            _statusBackground = CreateSection(statusX, sectionY, statusWidth, sectionHeight);
            _operationsBackground = CreateSection(
                operationsX, operationsY, operationsWidth, operationsHeight);
            _catalogAccent = CreateAccentBar(catalogX, sectionY, catalogWidth, 0.0025f);
            _statusAccent = CreateAccentBar(statusX, sectionY, statusWidth, 0.0025f);
            _operationsAccent = CreateAccentBar(
                operationsX, operationsY, operationsWidth, 0.0025f);

            _catalogHeader = CreateHeading(catalogX + 0.018f, sectionY + 0.010f,
                "MAIN CATALOG BOX", 0.31f);
            _catalogSubtitle = CreateHeading(catalogX + 0.018f, sectionY + 0.037f,
                "DEPARTMENT INFORMATION", 0.20f);
            _statusHeader = CreateHeading(statusX + 0.018f, sectionY + 0.010f,
                "STATUS & DISPATCH", 0.31f);
            _statusSubtitle = CreateHeading(statusX + 0.018f, sectionY + 0.037f,
                "OPERATIONAL DETAILS", 0.20f);
            _operationsHeader = CreateHeading(operationsX + 0.018f, operationsY + 0.008f,
                "ONGOING OPERATIONS", 0.31f);
            _operationsSubtitle = CreateHeading(operationsX + 0.240f, operationsY + 0.014f,
                "REAL-TIME FIELD ACTIVITY", 0.190f);
            _operationsWatermark = CreateRightAlignedText(
                operationsX + operationsWidth - 0.025f, operationsY + 0.061f,
                "LSPD", 0.50f, Color.FromArgb(55, 88, 191, 227));
            _operationsWatermarkSubtitle = CreateRightAlignedText(
                operationsX + operationsWidth - 0.025f, operationsY + 0.118f,
                "LOS SANTOS", 0.19f, Color.FromArgb(100, 180, 206, 224));

            _catalogHeader.Color = Color.White;
            _statusHeader.Color = Color.White;
            _operationsHeader.Color = Color.White;
            _catalogSubtitle.Color = Color.FromArgb(255, 165, 194, 219);
            _statusSubtitle.Color = Color.FromArgb(255, 165, 194, 219);
            _operationsSubtitle.Color = Color.FromArgb(255, 165, 194, 219);

            _departmentDetails = CreateDetails(catalogX + 0.018f, sectionY + 0.065f, 0.24f);
            _playerEntryBackground = CreateEntryBackground(
                catalogX + 0.012f, sectionY + 0.102f, catalogWidth - 0.024f, 0.058f);
            _backupEntryBackground = CreateEntryBackground(
                catalogX + 0.012f, sectionY + 0.169f, catalogWidth - 0.024f, 0.058f);
            _playerPortraitBackground = CreatePortraitBackground(
                catalogX + 0.022f, sectionY + 0.106f, 0.038f, 0.050f);
            _backupPortraitBackground = CreatePortraitBackground(
                catalogX + 0.022f, sectionY + 0.173f, 0.038f, 0.050f);
            _playerPortraitFallback = CreatePortraitFallback(
                catalogX + 0.022f, sectionY + 0.106f, 0.038f, 0.050f, "U");
            _backupPortraitFallback = CreatePortraitFallback(
                catalogX + 0.022f, sectionY + 0.173f, 0.038f, 0.050f, "B");
            _playerDetails = CreateDetails(catalogX + 0.071f, sectionY + 0.111f, 0.24f);
            _backupDetails = CreateDetails(catalogX + 0.071f, sectionY + 0.178f, 0.24f);
            _playerDetails.WordWrap = ScaledWidth(catalogWidth - 0.097f);
            _backupDetails.WordWrap = ScaledWidth(catalogWidth - 0.097f);

            string[] statusLabels =
            {
                "STATION:", "DISPATCH ACTIVITY:", "UNIT AGENCY:", "DUTY:", "STATUS:"
            };
            string[] statusValues = new string[statusLabels.Length];
            for (int i = 0; i < statusLabels.Length; i++)
            {
                float rowY = sectionY + 0.071f + (i * 0.031f);
                _statusRowBackgrounds[i] = CreateStatusRow(
                    statusX + 0.012f, rowY - 0.004f, statusWidth - 0.024f, 0.031f);
                _statusDots[i] = CreateStatusDot(statusX + 0.020f, rowY + 0.004f);
                _statusLabels[i] = CreateHeading(statusX + 0.038f, rowY,
                    statusLabels[i], 0.22f);
                _statusLabels[i].Color = Color.FromArgb(255, 165, 194, 219);
                _statusValues[i] = CreateDetails(statusX + 0.168f, rowY, 0.24f);
                _statusValues[i].WordWrap = ScaledWidth(statusWidth - 0.185f);
            }

            _citizenStatusDot = CreateStatusDot(operationsX + 0.021f, operationsY + 0.075f);
            _gangStatusDot = CreateStatusDot(operationsX + 0.021f, operationsY + 0.105f);
            _crimeStatusDot = CreateStatusDot(operationsX + 0.021f, operationsY + 0.135f);
            _citizenStatusDetails = CreateDetails(
                operationsX + 0.040f, operationsY + 0.067f, 0.24f);
            _gangStatusDetails = CreateDetails(
                operationsX + 0.040f, operationsY + 0.097f, 0.24f);
            _crimeStatusDetails = CreateDetails(
                operationsX + 0.040f, operationsY + 0.127f, 0.24f);
            _citizenStatusDetails.WordWrap = ScaledWidth(operationsWidth - 0.060f);
            _gangStatusDetails.WordWrap = ScaledWidth(operationsWidth - 0.060f);
            _crimeStatusDetails.WordWrap = ScaledWidth(operationsWidth - 0.060f);
        }

        internal bool AlmanacVisible { get; private set; }
        internal bool StationPreviewActive { get { return _previewSessionActive; } }

        internal enum StationPreviewMode
        {
            Wardrobe,
            Garage
        }

        /// <summary>
        /// Draws and checks one Police Station doorway trigger. The exterior
        /// entry point is linked from the Station record to its authored
        /// Location in LSPDImmersiveUtility.xml. This display never moves the
        /// Player.
        /// </summary>
        internal bool DrawStationEntry(
            LSPDPoliceStationDefinition station,
            LSPDPoliceLocationDefinition exteriorLocation)
        {
            if (station == null || exteriorLocation == null || !exteriorLocation.ExteriorSafe)
                return false;

            Ped player = Game.Player.Character;
            if (player == null || !player.Exists())
                return false;

            Vector3 position = new Vector3(exteriorLocation.X, exteriorLocation.Y, exteriorLocation.Z);
            float distance = player.Position.DistanceTo(position);
            if (distance <= StationMarkerRange)
            {
                World.DrawMarker(
                    MarkerType.Cylinder,
                    position + new Vector3(0f, 0f, -0.35f),
                    Vector3.Zero,
                    Vector3.Zero,
                    new Vector3(1.35f, 1.35f, 1.25f),
                    Color.FromArgb(105, 38, 158, 218),
                    false,
                    false,
                    false,
                    null,
                    null,
                    false);
                World.DrawMarker(
                    MarkerType.Cone,
                    position + new Vector3(0f, 0f, 1.7f),
                    Vector3.Zero,
                    new Vector3(180f, 0f, 0f),
                    new Vector3(0.38f, 0.38f, 0.55f),
                    Color.FromArgb(235, 235, 245, 255),
                    false,
                    false,
                    false,
                    null,
                    null,
                    false);
            }

            return !player.IsInVehicle() && distance <= StationEntryRadius;
        }

        internal void BeginStationPreview(
            StationPreviewMode mode,
            string pedModel,
            string vehicleModel)
        {
            _requestedPreviewMode = mode;
            _requestedPedModel = pedModel ?? string.Empty;
            _requestedVehicleModel = vehicleModel ?? string.Empty;
            _previewEnding = false;
            if (_previewSessionActive)
                return;

            _previousRenderingCamera = ScriptCameraDirector.RenderingCam;
            _previewSessionActive = true;
            _previewModeReady = false;
            BeginPreviewTransition();
        }

        internal void UpdateStationPreview(
            bool paused,
            StationPreviewMode mode,
            string pedModel,
            string vehicleModel)
        {
            if (!_previewSessionActive)
                return;

            _requestedPreviewMode = mode;
            _requestedPedModel = pedModel ?? string.Empty;
            _requestedVehicleModel = vehicleModel ?? string.Empty;

            DateTime now = DateTime.UtcNow;
            if (paused)
            {
                // A pause can stop the scene-switch tick after the screen has
                // faded out. Reveal the current view on the deadline, then
                // retry the pending switch after gameplay resumes.
                if (_previewTransitionWaiting && now >= _previewTransitionAtUtc)
                {
                    _previewTransitionWaiting = false;
                    FadeInPreview();
                }
                return;
            }

            if (_previewEnding)
            {
                if (now >= _previewTransitionAtUtc)
                    FinishStationPreview();
                return;
            }

            if (_previewTransitionWaiting)
            {
                if (now < _previewTransitionAtUtc)
                    return;

                _previewTransitionWaiting = false;
                try
                {
                    SwitchPreviewScene();
                    if (!_previewModeReady)
                        ResetPreviewSession();
                }
                catch
                {
                    ResetPreviewSession();
                    throw;
                }
                finally
                {
                    FadeInPreview();
                }
                return;
            }

            if (!_previewModeReady || _previewMode != _requestedPreviewMode)
            {
                BeginPreviewTransition();
                return;
            }

            EnsurePreviewSubject();
        }

        internal void EndStationPreview()
        {
            if (!_previewSessionActive || _previewEnding)
                return;

            _previewEnding = true;
            _previewTransitionWaiting = true;
            _previewTransitionAtUtc = DateTime.UtcNow.AddMilliseconds(PreviewFadeMilliseconds);
            FadeOutPreview();
        }

        internal void CancelStationPreview()
        {
            if (!_previewSessionActive)
                return;

            ResetPreviewSession();
            FadeInPreview();
        }

        private void ResetPreviewSession()
        {
            RestoreRenderingCamera();
            ClearPreviewFocus();
            DeletePreviewSubjects();
            DeletePreviewCamera();
            _previewSessionActive = false;
            _previewModeReady = false;
            _previewTransitionWaiting = false;
            _previewEnding = false;
        }

        private void BeginPreviewTransition()
        {
            _previewTransitionWaiting = true;
            _previewTransitionAtUtc = DateTime.UtcNow.AddMilliseconds(PreviewFadeMilliseconds);
            FadeOutPreview();
        }

        private void SwitchPreviewScene()
        {
            DeletePreviewSubjects();
            if (_previewCamera != null && _previewCamera.Exists())
                ScriptCameraDirector.StopRendering(false);
            DeletePreviewCamera();
            _previewMode = _requestedPreviewMode;
            if (_previewMode == StationPreviewMode.Wardrobe)
                CreatePreviewCamera(
                    WardrobeCameraPosition(),
                    PedPreviewPosition() + new Vector3(0f, 0f, 0.55f),
                    72f);
            else
                CreatePreviewCamera(
                    GarageCameraPosition(),
                    VehiclePreviewPosition() + new Vector3(0f, 0f, 0.65f),
                    48f);

            _previewModeReady = _previewCamera != null;
            if (_previewModeReady)
                EnsurePreviewSubject();
        }

        private void CreatePreviewCamera(Vector3 cameraPosition, Vector3 target, float fieldOfView)
        {
            try
            {
                // The documented preview rooms are at Mission Row. Keep the
                // Player at the selected station, but stream the remote camera
                // scene so its interior and preview subject are available.
                Function.Call(
                    Hash.SET_FOCUS_POS_AND_VEL,
                    cameraPosition.X,
                    cameraPosition.Y,
                    cameraPosition.Z,
                    0f,
                    0f,
                    0f);
                _previewFocusActive = true;
                RequestPreviewCollision(cameraPosition);
                RequestPreviewCollision(target);
                _previewCamera = Camera.Create(
                    ScriptedCameraNameHash.DefaultScriptedCamera,
                    cameraPosition,
                    Vector3.Zero,
                    fieldOfView,
                    false,
                    EulerRotationOrder.YXZ);
                if (_previewCamera == null || !_previewCamera.Exists())
                {
                    _previewCamera = null;
                    return;
                }

                _previewCamera.PointAt(target);
                // Build the shot first, then activate the scripted camera and
                // ask SHVDN to render it. A created camera alone is not shown.
                _previewCamera.IsActive = true;
                ScriptCameraDirector.StartRendering();
            }
            catch
            {
                DeletePreviewCamera();
            }
        }

        private void EnsurePreviewSubject()
        {
            if (_previewMode == StationPreviewMode.Wardrobe)
                EnsurePreviewPed();
            else
                EnsurePreviewVehicle();
        }

        private void EnsurePreviewPed()
        {
            if (_previewPed != null && _previewPed.Exists()
                && string.Equals(_previewPedModel, _requestedPedModel, StringComparison.OrdinalIgnoreCase))
                return;

            DeletePreviewPed();
            if (string.IsNullOrWhiteSpace(_requestedPedModel))
                return;

            Vector3 position = PedPreviewPosition();
            RequestPreviewCollision(position);
            Model model = new Model(_requestedPedModel);
            if (!model.IsValid || !model.IsPed)
                return;
            if (!model.IsLoaded)
            {
                model.Request();
                return;
            }

            Ped created = null;
            try
            {
                created = World.CreatePed(model, position);
                if (created == null || !created.Exists())
                    return;
                float groundZ;
                Vector3 groundNormal;
                if (World.GetGroundHeightAndNormal(position, out groundZ, out groundNormal))
                    created.Position = new Vector3(position.X, position.Y, groundZ);
                created.Heading = HeadingToward(position, WardrobeCameraPosition());
                created.IsInvincible = true;
                created.IsPersistent = true;
                created.BlockPermanentEvents = true;
                created.IsPositionFrozen = true;
                _previewPed = created;
                _previewPedModel = _requestedPedModel;
            }
            catch
            {
                if (created != null && created.Exists())
                    created.Delete();
            }
            finally
            {
                try { model.MarkAsNoLongerNeeded(); } catch { }
            }
        }

        private void EnsurePreviewVehicle()
        {
            if (_previewVehicle != null && _previewVehicle.Exists()
                && string.Equals(_previewVehicleModel, _requestedVehicleModel, StringComparison.OrdinalIgnoreCase))
                return;

            DeletePreviewVehicle();
            if (string.IsNullOrWhiteSpace(_requestedVehicleModel))
                return;

            Vector3 position = VehiclePreviewPosition();
            RequestPreviewCollision(position);
            float groundZ;
            Vector3 groundNormal;
            if (World.GetGroundHeightAndNormal(position, out groundZ, out groundNormal))
                position = new Vector3(position.X, position.Y, groundZ);
            Model model = new Model(_requestedVehicleModel);
            if (!model.IsValid || !model.IsVehicle)
                return;
            if (!model.IsLoaded)
            {
                model.Request();
                return;
            }

            Vehicle created = null;
            try
            {
                created = World.CreateVehicle(model, position, HeadingToward(position, GarageCameraPosition()));
                if (created == null || !created.Exists())
                    return;
                created.IsInvincible = true;
                created.IsPersistent = true;
                created.IsPositionFrozen = true;
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, created.Handle, false, true, true);
                created.PlaceOnGround();
                _previewVehicle = created;
                _previewVehicleModel = _requestedVehicleModel;
            }
            catch
            {
                if (created != null && created.Exists())
                    created.Delete();
            }
            finally
            {
                try { model.MarkAsNoLongerNeeded(); } catch { }
            }
        }

        private static Vector3 WardrobeCameraPosition()
        {
            return new Vector3(454.33f, -988.47f, 30.81f);
        }

        private static Vector3 PedPreviewPosition()
        {
            // CodeWalker locker-room staging spot supplied by the owner.
            return new Vector3(452.72f, -990.34f, 30.53f);
        }

        private static Vector3 GarageCameraPosition()
        {
            // CodeWalker garage preview camera supplied by the owner.
            return new Vector3(470.59f, -1019.98f, 31.44f);
        }

        private static Vector3 VehiclePreviewPosition()
        {
            // Owner-confirmed flat concrete spot for the open-garage preview.
            // This preview-only position is not a live Patrol or custody spawn point.
            return new Vector3(482.27f, -1020.92f, 30.24f);
        }

        private static float HeadingToward(Vector3 from, Vector3 to)
        {
            float heading = (float)(Math.Atan2(to.Y - from.Y, to.X - from.X) * (180.0 / Math.PI)) - 90f;
            heading %= 360f;
            return heading < 0f ? heading + 360f : heading;
        }

        private static void RequestPreviewCollision(Vector3 position)
        {
            try
            {
                Function.Call(
                    Hash.REQUEST_COLLISION_AT_COORD,
                    position.X,
                    position.Y,
                    position.Z);
            }
            catch { }
        }

        private void ClearPreviewFocus()
        {
            if (!_previewFocusActive)
                return;

            try { Function.Call(Hash.CLEAR_FOCUS); } catch { }
            _previewFocusActive = false;
        }

        private void FinishStationPreview()
        {
            ResetPreviewSession();
            FadeInPreview();
        }

        private static void FadeOutPreview()
        {
            try { GTA.UI.Screen.FadeOut(PreviewFadeMilliseconds); } catch { }
        }

        private static void FadeInPreview()
        {
            try { GTA.UI.Screen.FadeIn(PreviewFadeMilliseconds); } catch { }
        }

        private void RestoreRenderingCamera()
        {
            try
            {
                if (_previousRenderingCamera == null || !_previousRenderingCamera.Exists())
                    ScriptCameraDirector.StopRendering(true);
                else
                {
                    _previousRenderingCamera.IsActive = true;
                    ScriptCameraDirector.StartRendering();
                }
            }
            catch { }
            _previousRenderingCamera = null;
        }

        private void DeletePreviewSubjects()
        {
            DeletePreviewPed();
            DeletePreviewVehicle();
        }

        private void DeletePreviewPed()
        {
            try
            {
                if (_previewPed != null && _previewPed.Exists())
                    _previewPed.Delete();
            }
            catch { }
            _previewPed = null;
            _previewPedModel = string.Empty;
        }

        private void DeletePreviewVehicle()
        {
            try
            {
                if (_previewVehicle != null && _previewVehicle.Exists())
                    _previewVehicle.Delete();
            }
            catch { }
            _previewVehicle = null;
            _previewVehicleModel = string.Empty;
        }

        private void DeletePreviewCamera()
        {
            try
            {
                if (_previewCamera != null && _previewCamera.Exists())
                    _previewCamera.Delete();
            }
            catch { }
            _previewCamera = null;
        }

        internal void ToggleAlmanac()
        {
            AlmanacVisible = !AlmanacVisible;
            if (!AlmanacVisible)
                ReleaseAlmanacHeadshots();
        }

        internal void CloseAlmanac()
        {
            AlmanacVisible = false;
            ReleaseAlmanacHeadshots();
        }

        /// <summary>
        /// Draws the documented lower-corner Police status display. Every
        /// status is read from its current owner; this view never changes
        /// patrol, assignment, profile, or incident state.
        /// </summary>
        internal void DrawPoliceAlmanac(LSImmersivePoliceCore core, bool paused)
        {
            if (!AlmanacVisible || paused || core == null || !core.IsPoliceAuthorityActive)
                return;

            AlmanacValues values = ReadAlmanacValues(core);
            _agencyDetails.Text = Compact(values.AgencyTag, 32);
            _departmentDetails.Text = "DEPT: " + Compact(values.Department, 42);
            _playerDetails.Text = "OFF. " + Compact(values.PlayerOfficer, 20) + " [USER]\n"
                + "HASH " + values.PlayerModelHash;
            _backupDetails.Text = "OFF. " + Compact(values.BackupOfficer, 20) + " [BACK UP]\n"
                + "HASH " + values.BackupModelHash + " | " + values.BackupPresence;
            _statusValues[0].Text = Compact(values.Station, 30);
            _statusValues[1].Text = Compact(values.Dispatch, 30);
            _statusValues[2].Text = Compact(values.UnitAgency, 30);
            _statusValues[3].Text = Compact(values.Duty, 30);
            _statusValues[4].Text = Compact(values.OverallStatus, 30);
            _citizenStatusDetails.Text = "ACTIVE CITIZEN INTERACTION: "
                + Compact(values.CitizenActivity, 30);
            _gangStatusDetails.Text = "ACTIVE GANG INVESTIGATION: "
                + Compact(values.GangActivity, 30);
            _crimeStatusDetails.Text = "ACTIVE CRIME OBSERVATION: "
                + Compact(values.CrimeActivity, 30);

            _citizenStatusDot.Color = values.CitizenActive
                ? Color.FromArgb(255, 55, 184, 232) : Color.FromArgb(255, 125, 139, 154);
            _gangStatusDot.Color = values.GangActive
                ? Color.FromArgb(255, 255, 176, 40) : Color.FromArgb(255, 125, 139, 154);
            _crimeStatusDot.Color = values.CrimeActive
                ? Color.FromArgb(255, 242, 93, 93) : Color.FromArgb(255, 190, 202, 216);
            _statusDots[0].Color = Color.FromArgb(255, 88, 191, 227);
            _statusDots[1].Color = core.Dispatch.HasIncident
                ? Color.FromArgb(255, 242, 93, 93) : Color.FromArgb(255, 125, 139, 154);
            _statusDots[2].Color = core.Patrol.IsPatrolling
                ? Color.FromArgb(255, 55, 184, 232) : Color.FromArgb(255, 125, 139, 154);
            _statusDots[3].Color = core.Patrol.IsPatrolling
                ? Color.FromArgb(255, 68, 210, 133) : Color.FromArgb(255, 125, 139, 154);
            _statusDots[4].Color = core.Dispatch.HasIncident || values.CitizenActive
                ? Color.FromArgb(255, 68, 210, 133) : Color.FromArgb(255, 125, 139, 154);

            _background.Draw();
            _accent.Draw();
            _bottomAccent.Draw();
            _leftAccent.Draw();
            _rightAccent.Draw();
            _headerDivider.Draw();
            _catalogBackground.Draw();
            _statusBackground.Draw();
            _operationsBackground.Draw();
            _catalogAccent.Draw();
            _statusAccent.Draw();
            _operationsAccent.Draw();
            _operationsWatermark.Draw();
            _operationsWatermarkSubtitle.Draw();
            _playerEntryBackground.Draw();
            _backupEntryBackground.Draw();
            foreach (ScaledRectangle rowBackground in _statusRowBackgrounds)
                rowBackground.Draw();
            foreach (ScaledRectangle statusDot in _statusDots)
                statusDot.Draw();
            _playerPortraitBackground.Draw();
            _backupPortraitBackground.Draw();
            _title.Draw();
            _subtitle.Draw();
            _agencyHeader.Draw();
            _agencyDetails.Draw();
            _catalogHeader.Draw();
            _catalogSubtitle.Draw();
            _statusHeader.Draw();
            _statusSubtitle.Draw();
            _operationsHeader.Draw();
            _operationsSubtitle.Draw();
            if (!TryDrawHeadshot(values.PlayerPortrait, true))
                _playerPortraitFallback.Draw();
            if (!TryDrawHeadshot(values.BackupPortrait, false))
                _backupPortraitFallback.Draw();
            _departmentDetails.Draw();
            _playerDetails.Draw();
            _backupDetails.Draw();
            foreach (ScaledText statusLabel in _statusLabels)
                statusLabel.Draw();
            foreach (ScaledText statusValue in _statusValues)
                statusValue.Draw();
            _citizenStatusDot.Draw();
            _gangStatusDot.Draw();
            _crimeStatusDot.Draw();
            _citizenStatusDetails.Draw();
            _gangStatusDetails.Draw();
            _crimeStatusDetails.Draw();
        }

        private static AlmanacValues ReadAlmanacValues(LSImmersivePoliceCore core)
        {
            LSPDPoliceAgencyDefinition agency = core.Profile.FindAgency(
                core.Profile.Selection.AgencyId);
            LSPDPoliceStationDefinition station = core.Profile.FindStation(
                core.Profile.Selection.StationId);
            LSPDPoliceFavoriteModel backupFavorite = core.Profile.ActiveFavoriteBackupPed;
            string preferredBackupModelName = core.Profile.PreferredBackupPedModelName;
            LSPDPoliceModelDefinition backupModel = core.Profile.FindPed(preferredBackupModelName);
            string backupName = backupFavorite != null
                ? backupFavorite.DisplayName
                : backupModel != null
                    ? backupModel.DisplayName
                    : string.IsNullOrWhiteSpace(preferredBackupModelName)
                        ? "Not selected"
                        : preferredBackupModelName;
            Ped backupOfficer = FindActiveBackupOfficer(core);
            Ped player = Game.Player.Character;
            int selectedPlayerHash = ResolveModelHash(core.Profile.SelectedPedModelName);
            int playerHash = selectedPlayerHash != 0
                ? selectedPlayerHash
                : player != null && player.Exists() ? player.Model.Hash : 0;
            int backupHash = backupOfficer != null && backupOfficer.Exists()
                ? backupOfficer.Model.Hash : ResolveModelHash(preferredBackupModelName);
            if (backupOfficer != null && backupOfficer.Exists())
            {
                string activeBackupName = ResolvePedDisplayName(core.Profile, backupHash);
                if (!string.IsNullOrWhiteSpace(activeBackupName))
                    backupName = activeBackupName;
                else
                    backupName = "Active Backup Officer";
            }

            bool citizenActive = core.NpcResponse.HasActiveInteraction;
            bool gangActive = core.GangResponse.HasActiveIncident;
            bool crimeActive = core.CrimeActivity.Active;
            string dispatch = core.Dispatch.HasIncident
                ? BuildDispatchStatus(core)
                : core.Patrol.IsPatrolling ? "Standby" : "Not on patrol";
            string overallStatus = BuildOverallStatus(core);
            string citizenActivity = citizenActive
                ? core.NpcResponse.StatusText : "Standby";
            string gangActivity = gangActive
                ? core.GangResponse.StatusText : "Silent";
            string crimeActivity = crimeActive || core.CrimeActivity.HasAvailableActivity
                ? core.CrimeActivityStatus
                : "Yet to be observed";

            return new AlmanacValues
            {
                AgencyTag = agency == null || string.IsNullOrWhiteSpace(agency.Id)
                    ? "ACTIVE OPERATIONS | FIELD AREA"
                    : agency.Id.ToUpperInvariant() + " | LOS SANTOS",
                Department = agency == null ? "Not selected" : agency.DisplayName,
                PlayerOfficer = core.Profile.SelectedPedDisplayName,
                PlayerModelHash = FormatModelHash(playerHash),
                PlayerPortrait = player != null && player.Exists()
                    && selectedPlayerHash != 0 && player.Model.Hash == selectedPlayerHash
                        ? player : null,
                BackupOfficer = backupName,
                BackupModelHash = FormatModelHash(backupHash),
                BackupPresence = backupOfficer == null ? "STANDBY" : "ACTIVE",
                BackupPortrait = backupOfficer,
                Station = station == null ? "Not selected" : station.DisplayName,
                Dispatch = dispatch,
                UnitAgency = agency == null
                    ? "Police Standby"
                    : (core.Patrol.IsPatrolling ? "Active " : "Standby ")
                        + Compact(agency.Id.ToUpperInvariant(), 8) + " Team",
                Duty = core.Patrol.IsPatrolling ? "On Patrol" : "On Duty / Standby",
                OverallStatus = overallStatus,
                CitizenActivity = citizenActivity,
                GangActivity = gangActivity,
                CrimeActivity = crimeActivity,
                CitizenActive = citizenActive,
                GangActive = gangActive,
                CrimeActive = crimeActive
            };
        }

        private static string BuildDispatchStatus(LSImmersivePoliceCore core)
        {
            if (core.Dispatch.Current == null)
                return core.Dispatch.CurrentStatus;
            string title = core.Dispatch.CurrentTitle;
            return string.IsNullOrWhiteSpace(title)
                ? core.Dispatch.State.ToString()
                : Compact(title, 17) + " | " + core.Dispatch.State;
        }

        private static string BuildOverallStatus(LSImmersivePoliceCore core)
        {
            if (core.Dispatch.HasIncident)
                return "Dispatch " + core.Dispatch.State;
            if (core.NpcResponse.HasActiveInteraction)
                return "Citizen Interaction";
            if (core.GangResponse.HasActiveIncident)
                return "Gang Investigation";
            if (core.CrimeActivity.Active)
                return core.CrimeActivity.State.ToString();
            return core.Patrol.IsPatrolling ? "On Patrol" : "On Duty / Standby";
        }

        private static Ped FindActiveBackupOfficer(LSImmersivePoliceCore core)
        {
            if (core == null || core.Backup == null)
                return null;
            try
            {
                foreach (Ped officer in core.Backup.ProtectedActors)
                    if (officer != null && officer.Exists() && !officer.IsDead)
                        return officer;
            }
            catch { }
            return null;
        }

        private static int ResolveModelHash(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
                return 0;
            try { return new Model(modelName).Hash; }
            catch { return 0; }
        }

        private static string ResolvePedDisplayName(LSPDProfile profile, int modelHash)
        {
            if (profile == null || modelHash == 0)
                return string.Empty;
            try
            {
                foreach (LSPDPoliceModelDefinition candidate in profile.PedModels)
                    if (candidate != null && ResolveModelHash(candidate.ModelName) == modelHash)
                        return candidate.DisplayName;
                foreach (LSPDPoliceFavoriteModel candidate in profile.FavoriteBackupPeds)
                    if (candidate != null && ResolveModelHash(candidate.ModelName) == modelHash)
                        return candidate.DisplayName;
            }
            catch { }
            return string.Empty;
        }

        private static string FormatModelHash(int hash)
        {
            return hash == 0 ? "Unavailable" : "0x" + unchecked((uint)hash).ToString("X8");
        }

        private bool TryDrawHeadshot(Ped ped, bool playerPortrait)
        {
            int handle = playerPortrait ? _playerHeadshotHandle : _backupHeadshotHandle;
            int pedHandle = playerPortrait ? _playerHeadshotPedHandle : _backupHeadshotPedHandle;
            int modelHash = playerPortrait ? _playerHeadshotModelHash : _backupHeadshotModelHash;
            DateTime retryAt = playerPortrait ? _playerHeadshotRetryAtUtc : _backupHeadshotRetryAtUtc;

            if (ped == null || !ped.Exists())
            {
                ReleaseHeadshot(playerPortrait);
                return false;
            }

            int currentModelHash = ped.Model.Hash;
            if (handle != 0 && (ped.Handle != pedHandle || currentModelHash != modelHash))
            {
                ReleaseHeadshot(playerPortrait);
                handle = 0;
                retryAt = DateTime.MinValue;
            }

            if (handle == 0)
            {
                if (DateTime.UtcNow < retryAt)
                    return false;
                try
                {
                    handle = Function.Call<int>(Hash.REGISTER_PEDHEADSHOT, ped.Handle);
                    if (handle == 0)
                    {
                        retryAt = DateTime.UtcNow.AddSeconds(2);
                        StoreHeadshot(playerPortrait, 0, 0, 0, retryAt);
                        return false;
                    }
                    pedHandle = ped.Handle;
                    modelHash = currentModelHash;
                    retryAt = DateTime.MinValue;
                    StoreHeadshot(playerPortrait, handle, pedHandle, modelHash, retryAt);
                }
                catch
                {
                    retryAt = DateTime.UtcNow.AddSeconds(2);
                    StoreHeadshot(playerPortrait, 0, 0, 0, retryAt);
                    return false;
                }
            }

            try
            {
                if (!Function.Call<bool>(Hash.IS_PEDHEADSHOT_VALID, handle))
                {
                    ReleaseHeadshot(playerPortrait);
                    StoreHeadshot(
                        playerPortrait,
                        0,
                        0,
                        0,
                        DateTime.UtcNow.AddSeconds(2));
                    return false;
                }
                if (!Function.Call<bool>(Hash.IS_PEDHEADSHOT_READY, handle))
                    return false;
                string dictionary = Function.Call<string>(Hash.GET_PEDHEADSHOT_TXD_STRING, handle);
                if (string.IsNullOrWhiteSpace(dictionary))
                    return false;
                float x = AlmanacLeft + 0.055f;
                float y = playerPortrait ? AlmanacTop + 0.217f : AlmanacTop + 0.284f;
                Function.Call(Hash.DRAW_SPRITE, dictionary, dictionary,
                    x, y, 0.035f, 0.046f, 0.0f, 255, 255, 255, 255);
                return true;
            }
            catch { return false; }
        }

        private void StoreHeadshot(
            bool playerPortrait,
            int handle,
            int pedHandle,
            int modelHash,
            DateTime retryAt)
        {
            if (playerPortrait)
            {
                _playerHeadshotHandle = handle;
                _playerHeadshotPedHandle = pedHandle;
                _playerHeadshotModelHash = modelHash;
                _playerHeadshotRetryAtUtc = retryAt;
            }
            else
            {
                _backupHeadshotHandle = handle;
                _backupHeadshotPedHandle = pedHandle;
                _backupHeadshotModelHash = modelHash;
                _backupHeadshotRetryAtUtc = retryAt;
            }
        }

        private void ReleaseHeadshot(bool playerPortrait)
        {
            int handle = playerPortrait ? _playerHeadshotHandle : _backupHeadshotHandle;
            if (handle != 0)
            {
                try { Function.Call(Hash.UNREGISTER_PEDHEADSHOT, handle); }
                catch { }
            }
            StoreHeadshot(playerPortrait, 0, 0, 0, DateTime.MinValue);
        }

        private void ReleaseAlmanacHeadshots()
        {
            ReleaseHeadshot(true);
            ReleaseHeadshot(false);
        }

        private sealed class AlmanacValues
        {
            internal string AgencyTag;
            internal string Department;
            internal string PlayerOfficer;
            internal string PlayerModelHash;
            internal Ped PlayerPortrait;
            internal string BackupOfficer;
            internal string BackupModelHash;
            internal string BackupPresence;
            internal Ped BackupPortrait;
            internal string Station;
            internal string Dispatch;
            internal string UnitAgency;
            internal string Duty;
            internal string OverallStatus;
            internal string CitizenActivity;
            internal string GangActivity;
            internal string CrimeActivity;
            internal bool CitizenActive;
            internal bool GangActive;
            internal bool CrimeActive;
        }

        private static ScaledRectangle CreateAccentBar(float x, float y, float width, float height)
        {
            ScaledRectangle accent = new ScaledRectangle(
                ScaledPoint(x, y), ScaledSize(width, height));
            accent.Color = Color.FromArgb(255, 35, 166, 231);
            return accent;
        }

        private static ScaledRectangle CreateEntryBackground(
            float x, float y, float width, float height)
        {
            ScaledRectangle entry = new ScaledRectangle(
                ScaledPoint(x, y), ScaledSize(width, height));
            entry.Color = Color.FromArgb(135, 19, 42, 60);
            return entry;
        }

        private static ScaledRectangle CreateStatusRow(
            float x, float y, float width, float height)
        {
            ScaledRectangle row = new ScaledRectangle(
                ScaledPoint(x, y), ScaledSize(width, height));
            row.Color = Color.FromArgb(100, 22, 47, 67);
            return row;
        }

        private static ScaledText CreateRightAlignedText(
            float x, float y, string text, float scale, Color color)
        {
            ScaledText rightAligned = new ScaledText(
                ScaledPoint(x, y), text, scale, GTA.UI.Font.ChaletLondon);
            rightAligned.Alignment = GTA.UI.Alignment.Right;
            rightAligned.Color = color;
            rightAligned.Shadow = true;
            return rightAligned;
        }

        private static ScaledRectangle CreateSection(float x, float y, float width, float height)
        {
            ScaledRectangle section = new ScaledRectangle(
                ScaledPoint(x, y),
                ScaledSize(width, height));
            section.Color = Color.FromArgb(200, 9, 20, 34);
            return section;
        }

        private static ScaledRectangle CreatePortraitBackground(
            float x, float y, float width, float height)
        {
            ScaledRectangle portrait = new ScaledRectangle(
                ScaledPoint(x, y), ScaledSize(width, height));
            portrait.Color = Color.FromArgb(255, 16, 65, 92);
            return portrait;
        }

        private static ScaledRectangle CreateStatusDot(float x, float y)
        {
            ScaledRectangle dot = new ScaledRectangle(
                ScaledPoint(x, y), ScaledSize(0.0065f, 0.011f));
            dot.Color = Color.FromArgb(255, 125, 139, 154);
            return dot;
        }

        private static ScaledText CreatePortraitFallback(
            float x, float y, float width, float height, string initial)
        {
            ScaledText fallback = new ScaledText(
                ScaledPoint(x + width * 0.30f, y + height * 0.10f),
                initial,
                0.26f,
                GTA.UI.Font.ChaletLondon);
            fallback.Color = string.Equals(initial, "U", StringComparison.Ordinal)
                ? Color.FromArgb(255, 87, 195, 236)
                : Color.FromArgb(255, 70, 151, 190);
            fallback.Shadow = true;
            return fallback;
        }

        private static ScaledText CreateHeading(float x, float y, string text, float scale)
        {
            ScaledText heading = new ScaledText(
                ScaledPoint(x, y),
                text,
                scale,
                GTA.UI.Font.ChaletLondon);
            heading.Color = Color.FromArgb(255, 88, 191, 227);
            heading.Shadow = true;
            return heading;
        }

        private static ScaledText CreateDetails(float x, float y, float scale)
        {
            ScaledText details = new ScaledText(
                ScaledPoint(x, y),
                string.Empty,
                scale,
                GTA.UI.Font.ChaletLondon);
            details.Color = Color.White;
            details.Shadow = true;
            return details;
        }

        private static PointF ScaledPoint(float relativeX, float relativeY)
        {
            return new PointF(relativeX, relativeY).ToScaled();
        }

        private static SizeF ScaledSize(float relativeWidth, float relativeHeight)
        {
            return new SizeF(relativeWidth, relativeHeight).ToScaled();
        }

        private static float ScaledWidth(float relativeWidth)
        {
            return relativeWidth.ToXScaled();
        }

        private static string Compact(string value, int maximumLength)
        {
            string text = string.IsNullOrWhiteSpace(value)
                ? "No status available."
                : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length <= maximumLength)
                return text;
            int cut = text.LastIndexOf(' ', Math.Max(0, maximumLength - 2));
            if (cut < Math.Max(4, maximumLength / 2))
                cut = maximumLength - 1;
            return text.Substring(0, cut).TrimEnd() + "…";
        }
    }
}
