# New code and XML elements only

Workspace: github.com/anyigapasin/LSImmersiveLife
Base: 738c25e2cdfa9def79442407daf788f87e995083

These are changed fragments. The edited-source archive contains the complete files.

## LSImmersiveDictionaryAnimation.cs

Changed code beginning at line 13:

```csharp
    // Semantic references shared by the Dispatch scene owner. Availability and
    // playback are separate checks: callers confirm on a later game tick.
    internal static bool TryGetDispatchAction(string action, out string dictionary, out string clip)
    {
        dictionary = null; clip = null;
        switch (action)
        {
            case "exchange_giver": dictionary = "mp_common"; clip = "givetake1_a"; break;
            case "exchange_receiver": dictionary = "mp_common"; clip = "givetake1_b"; break;
            case "conversation": dictionary = "misscarsteal4@actor"; clip = "actor_berating_loop"; break;
            case "cower": dictionary = "amb@code_human_cower_stand@male@base"; clip = "base"; break;
            case "camera": dictionary = "amb@world_human_paparazzi@male@base"; clip = "base"; break;
            case "intoxicated": dictionary = "amb@world_human_bum_standing@drunk@idle_a"; clip = "idle_a"; break;
            case "lying": dictionary = "amb@world_human_bum_slumped@male@laying_on_left_side@base"; clip = "base"; break;
            case "threat": dictionary = "misscarsteal4@actor"; clip = "actor_berating_loop"; break;
            case "surrender": dictionary = "random@arrests"; clip = "idle_2_hands_up"; break;
            default: return false;
        }
        return true;
    }

    internal static bool RequestDispatchAction(string action)
    {
        string dictionary, clip;
        float duration;
        return TryGetDispatchAction(action, out dictionary, out clip)
            && TryGetClipDuration(dictionary, clip, out duration);
    }

    internal static bool TryPlayDispatchAction(Ped actor, string action, bool loop)
    {
        string dictionary, clip;
        float duration;
        if (!TryGetDispatchAction(action, out dictionary, out clip)) return false;
        if (IsPlaying(actor, dictionary, clip)) return true;
        return TryPlay(actor, dictionary, clip, 4f, -4f, loop ? -1 : 4500,
            loop ? 1 : 0, 1f, out duration);
    }

    internal static bool IsDispatchActionPlaying(Ped actor, string action)
    {
        string dictionary, clip;
        return TryGetDispatchAction(action, out dictionary, out clip)
            && IsPlaying(actor, dictionary, clip);
    }

```

Changed code beginning at line 304:

```csharp
            if (!Function.Call<bool>(Hash.DOES_ANIM_DICT_EXIST, dictionary)) return false;
```

## LSImmersiveMainConfig.cs

Changed code beginning at line 726:

```csharp
        internal static LSPoliceDispatchSettings Default() { return new LSPoliceDispatchSettings { Enabled = true, MinimumQuietPatrolSeconds = 180, MaximumQuietPatrolSeconds = 600, RepeatProtectionMinutes = 20, MaximumActiveIncidents = 1, MaximumSuspects = 5, AudioMinimumGapSeconds = 5, ContextClearQuietSeconds = 30, PlayerRadiusFallbackMinimumDistanceMeters = 80, PlayerRadiusFallbackMaximumDistanceMeters = 150 }; }
```

## LSImmersiveMainUI.xml

Changed code beginning at line 30:

```xml
    <Dispatch enabled="true" minimumQuietPatrolSeconds="180" maximumQuietPatrolSeconds="600" repeatProtectionMinutes="20" maximumActiveIncidents="1" maximumSuspects="5" audioMinimumGapSeconds="5" contextClearQuietSeconds="30" playerRadiusFallbackMinimumDistanceMeters="80" playerRadiusFallbackMaximumDistanceMeters="150" />
```

## LSImmersivePoliceCore.cs

Changed code beginning at line 889:

```csharp
                && Dispatch.HasAnyConvoyCustody)
```

Changed code beginning at line 893:

```csharp
                if (!Dispatch.HasIncident) Backup.BeginStandDown(
```

Changed code beginning at line 1153:

```csharp
            if (Dispatch.HasAnyConvoyCustody)
```

Changed code beginning at line 1160:

```csharp
            if (!Dispatch.HasIncident) Backup.BeginStandDown("Custody operation completed.");
```

Changed code beginning at line 4068:

```csharp
            if (Convoy.Completed && Dispatch.HasAnyConvoyCustody)
```

Changed code beginning at line 4090:

```csharp
            if (Convoy.Completed && Dispatch.HasAnyConvoyCustody)
```

## LSPDAudioDispatch.cs

Changed code beginning at line 43:

```csharp
        private Dictionary<string, string> _incidentRecordingTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
```

Changed code beginning at line 135:

```csharp
            var incidentRecordingTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var reusedEvents = new Dictionary<string, string>(StringComparer.Ordinal);
```

Changed code beginning at line 230:

```csharp
                    string reuse = (string)node.Attribute("reuseEvent");
                    if (!string.IsNullOrWhiteSpace(reuse))
                    {
                        if (entry.Clips.Count > 0) throw new InvalidDataException("A reused audio event cannot also declare clips: " + id);
                        reusedEvents.Add(id, reuse);
                    }
```

Changed code beginning at line 237:

```csharp
                }
                foreach (var reuse in reusedEvents)
                {
                    AudioEvent source;
                    if (reuse.Key == reuse.Value || reusedEvents.ContainsKey(reuse.Value)
                        || !events.TryGetValue(reuse.Value, out source) || source.Clips.Count == 0)
                        throw new InvalidDataException("Invalid recording reuse target: " + reuse.Value);
                    if (source.Speaker != events[reuse.Key].Speaker)
                        throw new InvalidDataException("Recording reuse must retain its actual speaker.");
                    events[reuse.Key].Clips.AddRange(source.Clips);
                }
                foreach (XElement alias in root.Elements("IncidentAliases").Elements("Alias"))
                {
                    string type = Required(alias, "type"), recording = Required(alias, "recordingType");
                    if (incidentRecordingTypes.ContainsKey(type)
                        || !events.Values.SelectMany(e => e.Clips).Any(c => c.Kind == recording))
                        throw new InvalidDataException("Invalid incident recording alias: " + type);
                    incidentRecordingTypes.Add(type, recording);
```

Changed code beginning at line 264:

```csharp
                    _events = events; _incidentRecordingTypes = incidentRecordingTypes;
                    _capacity = capacity; _language = language;
```

Changed code beginning at line 313:

```csharp
                string recordingType;
                string resolvedKind = incidentType ?? "";
                if (_incidentRecordingTypes.TryGetValue(resolvedKind, out recordingType))
                {
                    WarnOnce("recording-fallback:" + resolvedKind,
                        "No dedicated recording for " + resolvedKind + "; using catalog meaning " + recordingType + ".");
                    resolvedKind = recordingType;
                }
```

Changed code beginning at line 323:

```csharp
                    Event = entry, Scope = scopeId, Kind = resolvedKind, Agency = agency ?? "",
```

## LSPDBackUp.cs

Changed code beginning at line 8978:

```csharp
                if (targets.Count >= 5)
```

## LSPDCriminalProfile.xml

Changed code beginning at line 29:

```xml
      <Profile id="dispatch_naked_subject" model="a_m_y_stbla_01" vehicleModel="stanier" weapon="WEAPON_UNARMED" armed="false" disposition="situational" pedPoolId="dispatch_naked_peds" vehiclePoolId="dispatch_street_vehicles" weaponPoolId="dispatch_unarmed_weapons" convoyPedPoolId="convoy_prisoner_peds" convoyVehiclePoolId="convoy_threat_vehicles" convoyWeaponPoolId="convoy_threat_weapons" weight="5" dispatchEligible="true" crimeActivityEligible="false" convoyEligible="false" />
    <Profile id="dispatch_knife_assailant" model="a_m_y_stbla_01" vehicleModel="stanier" weapon="WEAPON_UNARMED" armed="true" disposition="situational" pedPoolId="dispatch_armed_peds" vehiclePoolId="dispatch_street_vehicles" weaponPoolId="dispatch_knife_weapons" convoyPedPoolId="convoy_prisoner_peds" convoyVehiclePoolId="convoy_threat_vehicles" convoyWeaponPoolId="convoy_threat_weapons" weight="5" dispatchEligible="true" crimeActivityEligible="false" convoyEligible="false" />
```

Changed code beginning at line 2986:

```xml
      <Pool id="dispatch_naked_peds" assetType="ped" useFor="dispatch"><Ref id="criminal_ped_085" /></Pool>
    <Pool id="dispatch_knife_weapons" assetType="weapon" useFor="dispatch"><Ref id="criminal_weapon_002" /></Pool>
```

## LSPDDispatch.cs

Changed code beginning at line 2696:

```csharp
                if (_ambientWorld != null && !_ambientWorld.PrepareDispatchActivityResources(incident))
                {
                    reason = "Stage=ActivityResources; Result=DictionaryOrClipNotLoaded";
                    incident.ScenePreparationNextRetryAt = DateTime.UtcNow.AddMilliseconds(750);
                    return DateTime.UtcNow < incident.ScenePreparationDeadline
                        ? SceneOfferPreflightStatus.Pending : SceneOfferPreflightStatus.Failed;
                }
```

Changed code beginning at line 3186:

```csharp
            Ped investigationVictim = PendingAuthoredMurderVictims(_incident)
                .OrderBy(p => p.Position.DistanceTo(player.Position)).FirstOrDefault();
```

Changed code beginning at line 3209:

```csharp
            if (HasPendingVictimInvestigation)
                return "Victim examination recorded. Investigate the remaining authored victims with I before transport or completion.";
```

Changed code beginning at line 3234:

```csharp
        private static IEnumerable<Ped> PendingAuthoredMurderVictims(LSPDDispatchEvent incident)
        {
            if (incident == null || !incident.VictimStartsDead) return Enumerable.Empty<Ped>();
            return new[] { incident.Victim }.Concat(incident.AdditionalParticipants.Take(incident.AdditionalDeadVictims))
                .Where(p => p != null && p.Exists() && !incident.InvestigatedVictimHandles.Contains(p.Handle));
        }

```

Changed code beginning at line 3248:

```csharp
            Ped victim = PendingAuthoredMurderVictims(incident)
                .OrderBy(p => player == null ? 0f : p.Position.DistanceTo(player.Position)).FirstOrDefault();
```

Changed code beginning at line 3312:

```csharp
            incident.InvestigatedVictimHandles.Add(victim.Handle);
            incident.VictimInvestigated = incident.InvestigatedVictimHandles.Count >= 1 + incident.AdditionalDeadVictims;
```

Changed code beginning at line 3731:

```csharp
            incident.ResolvedSuspectHandles.Add(releasedHandle);
```

Changed code beginning at line 8406:

```csharp
            bool allLivingTransferred = LiveSuspects(_incident).All(p =>
                _incident.ConvoyCustodySuspectHandles.Contains(p.Handle));
            _incident.OwnedByDispatch = !allLivingTransferred;
            _incident.HasConvoyCustodyHandoff = allLivingTransferred;
            if (allLivingTransferred) _incident.SurrenderRequested = false;
```

Changed code beginning at line 8437:

```csharp
            if (allLivingTransferred)
            {
                CleanupBlip(_sceneBlip);
                CleanupSuspectBlips();
                _sceneBlip = null;
            }
            else SetStateForUnresolvedSuspects(LSPDDispatchState.Investigating);
```

Changed code beginning at line 8554:

```csharp
                List<Ped> retainedArrested = _incident.ArrestedSuspects
                    .Where(p => p != null && p.Exists() && !p.IsDead).Concat(valid)
                    .GroupBy(p => p.Handle).Select(g => g.First()).ToList();
```

Changed code beginning at line 8558:

```csharp
                _incident.ArrestedSuspects.AddRange(retainedArrested);
                // Other suspects retain their individual compliance and explicit
                // secure decisions while this physical custody subset recovers.
```

Changed code beginning at line 8655:

```csharp
        internal bool HasAnyConvoyCustody
        {
            get { return _incident != null && _incident.ConvoyCustodySuspectHandles.Count > 0; }
        }

```

Changed code beginning at line 8662:

```csharp
            return ResolveCompletedConvoyCustody("Prisoner delivered and booking completed.");
```

Changed code beginning at line 8667:

```csharp
            return ResolveCompletedConvoyCustody("Transport officers have physically assumed the loaded custody group.");
        }

        private string ResolveCompletedConvoyCustody(string result)
        {
            if (_incident == null || !HasAnyConvoyCustody) return "No linked Dispatch custody remains.";
            LSPDDispatchEvent incident = _incident;
            var transferred = new HashSet<int>(incident.ConvoyCustodySuspectHandles);
            incident.ResolvedSuspectHandles.UnionWith(transferred);
            foreach (int handle in transferred)
            {
                _managedSuspectPresenceObservations.Remove(handle);
                _managedActorLostSuspectHandles.Remove(handle);
                _lastCompliantTaskAt.Remove(handle);
                ClearSecuredPoseTracking(handle);
                incident.SuspectResponses.Remove(handle);
                incident.ActivityTaskIssuedAt.Remove(handle);
            }
            incident.Suspects.RemoveAll(p => p == null || transferred.Contains(p.Handle));
            incident.ArrestedSuspects.RemoveAll(p => p == null || transferred.Contains(p.Handle));
            incident.CompliantSuspects.RemoveAll(p => p == null || transferred.Contains(p.Handle));
            if (incident.Suspect != null && transferred.Contains(incident.Suspect.Handle))
                incident.Suspect = incident.Suspects.FirstOrDefault(p => p != null && p.Exists() && !p.IsDead);
            incident.ConvoyCustodySuspectHandles.Clear();
            incident.HasConvoyCustodyHandoff = false;
            incident.OwnedByDispatch = true;
            if (LiveSuspects(incident).Any() || HasPendingVictimInvestigation)
            {
                SetStateForUnresolvedSuspects(LSPDDispatchState.Investigating);
                return result + " Other suspects or investigation remain; Dispatch stays active.";
            }
            Complete(result);
            return result + " Dispatch completed.";
```

Changed code beginning at line 8845:

```csharp
                    bool authoredVehicleStopResponse = (IsVehicleEntryActivity(incident.VehicleActivity) || IsAuthoredDrivingActivity(incident))
```

Changed code beginning at line 9243:

```csharp
            if (!MaintainAuthoredDispatchActivity(now, player)) return;
```

Changed code beginning at line 9259:

```csharp
                    || IsVehicleEntryActivity(_incident.VehicleActivity) || IsAuthoredDrivingActivity(_incident))
```

Changed code beginning at line 9262:

```csharp
                && ((IsVehicleEntryActivity(_incident.VehicleActivity) || IsAuthoredDrivingActivity(_incident))
```

Changed code beginning at line 9275:

```csharp
                    if (string.IsNullOrEmpty(_incident.PreparedActivity))
                    {
                        TryStartInteriorStoreRobberyFlight(player);
                        MaintainStagedSceneBehavior(now);
                        MaintainVehicleSceneActivity(now, player);
                    }
```

Changed code beginning at line 9385:

```csharp
            return string.Equals(sceneBehavior, "interior_activity", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sceneBehavior, "interior_store_custody",
```

Changed code beginning at line 11097:

```csharp
        private bool MaintainAuthoredDispatchActivity(DateTime now, Ped player)
        {
            if (_incident == null || string.IsNullOrEmpty(_incident.PreparedActivity)) return true;
            string reason = "AmbientWorld is unavailable";
            if (_ambientWorld == null) { Fail(reason); return false; }
            bool confirmed = _ambientWorld.MaintainPreparedDispatchActivity(_incident, now, out reason);
            if (!confirmed && reason.StartsWith("FAILED:", StringComparison.Ordinal))
            {
                Fail("The incident could not establish its authored activity after bounded recovery. " + reason);
                return false;
            }
            if (confirmed && !_incident.PreparedActivityAudioReported)
            {
                _incident.PreparedActivityAudioReported = true;
                ReportAudioStage("lsimmersivelife.police.scene.activity_confirmed", _incident, "activity-confirmed", true);
            }
            if (IsAuthoredDrivingActivity(_incident) && _incident.Suspect != null && _incident.Suspect.Exists())
            {
                // Location follows the actual lead car; passengers and the second
                // driver remain in the same occurrence and existing pursuit roster.
                _incident.Origin = _incident.Suspect.Position;
                if (!_incident.PlayerEncounterResponseStarted && player != null && player.Exists()
                    && LiveSuspects(_incident).Any(p => player.Position.DistanceTo(p.Position) < 45f))
                {
                    foreach (Ped actor in LiveSuspects(_incident))
                    {
                        _incident.SuspectResponses[actor.Handle] = "flee";
                        BeginDispatchVehicleFlightProtection(actor, _incident);
                        CapturePursuitVehicleHealthBaseline(_incident, actor);
                    }
                    _incident.PlayerEncounterResponseStarted = true;
                    SetStateForUnresolvedSuspects(LSPDDispatchState.SuspectFleeing);
                    ReportAudioStage("lsimmersivelife.police.pursuit.continuing", _incident, "vehicle-encounter", true);
                }
                // Each stopped driver/passenger can enter its own foot response.
                if (_incident.PlayerEncounterResponseStarted)
                    foreach (Ped actor in LiveSuspects(_incident).ToList())
                        MaintainStoppedPursuitVehicle(_incident, actor, player, now);
            }
            return true;
        }

        private static bool IsAuthoredDrivingActivity(LSPDDispatchEvent incident)
        {
            return incident != null && !string.IsNullOrEmpty(incident.PreparedActivity)
                && (incident.PreparedActivity.StartsWith("driving_", StringComparison.Ordinal)
                    || incident.PreparedActivity == "ramming");
        }

```

Changed code beginning at line 11153:

```csharp

            if (!string.IsNullOrEmpty(_incident.PreparedActivity))
            {
                _incident.SceneBehaviorInitialized = true;
                _incident.PreArrivalSceneBehaviorInitialized = true;
                _incident.SceneBehaviorNextStepAt = DateTime.MaxValue;
                string activityReason;
                if (_ambientWorld != null)
                    _ambientWorld.MaintainPreparedDispatchActivity(_incident, DateTime.UtcNow, out activityReason);
                return;
            }
```

Changed code beginning at line 12231:

```csharp
            if (incident == null || (!IsAuthoredDrivingActivity(incident) && !IsVehicleEntryActivity(incident.VehicleActivity))
                || (!IsAuthoredDrivingActivity(incident) && incident.VehicleActivityStage < 3)
```

Changed code beginning at line 12257:

```csharp
                && (IsVehicleEntryActivity(incident.VehicleActivity) || IsAuthoredDrivingActivity(incident));
```

Changed code beginning at line 13873:

```csharp
            int required = Math.Max(0, Math.Min(5, incident.SceneParticipantCount));
```

Changed code beginning at line 13877:

```csharp
                || (incident.SceneResolution.IsInteriorScene && incident.SceneResolution.LiveInteriorId == 0)
```

Changed code beginning at line 13889:

```csharp
                    || !LSPDDispatchEventDefinition.IsVictimStateReady(
                        index < incident.AdditionalDeadVictims, participant.IsDead, participant.Health)
```

Changed code beginning at line 14093:

```csharp
                        || !LSPDDispatchEventDefinition.IsVictimStateReady(
                            incident.AdditionalParticipants.IndexOf(participant) < incident.AdditionalDeadVictims,
                            participant.IsDead, participant.Health))
```

Changed code beginning at line 14984:

```csharp
            int required = Math.Max(0, Math.Min(5, incident.SceneParticipantCount));
```

Changed code beginning at line 14988:

```csharp
                || (incident.SceneResolution.IsInteriorScene && incident.SceneResolution.LiveInteriorId == 0)
```

Changed code beginning at line 15021:

```csharp
                    bool ready = LSPDDispatchEventDefinition.IsVictimStateReady(
                            index < incident.AdditionalDeadVictims, participant.IsDead, participant.Health)
```

Changed code beginning at line 15073:

```csharp
                if (participant == null && index >= incident.AdditionalDeadVictims
```

Changed code beginning at line 15199:

```csharp
                            index < incident.AdditionalDeadVictims,
```

Changed code beginning at line 15258:

```csharp
                if (!string.IsNullOrEmpty(incident.PreparedActivity)) continue;
```

Changed code beginning at line 15393:

```csharp
                    VehicleSeat assignedSeat = VehicleSeat.Driver;
                    if (assignedVehicle == null && incident.RequiresVehicle
                        && !IsVehicleEntryActivity(incident.VehicleActivity)
                        && incident.SceneResolution != null && !incident.SceneResolution.IsInteriorScene)
                    {
                        assignedVehicle = incident.SuspectVehicles[index % incident.SuspectVehicles.Count];
                        int capacity = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, assignedVehicle);
                        int seat = Enumerable.Range(0, Math.Max(0, capacity)).Where(
                            value => Function.Call<bool>(Hash.IS_VEHICLE_SEAT_FREE, assignedVehicle, value, false))
                            .DefaultIfEmpty(-99).First();
                        if (seat == -99) { failureReason = "Insufficient real passenger seats for the authored group."; break; }
                        assignedSeat = (VehicleSeat)seat;
                    }
```

Changed code beginning at line 15413:

```csharp
                        ? assignedVehicle.CreatePedOnSeat(assignedSeat, suspectModel)
```

Changed code beginning at line 16562:

```csharp
            if (LiveSuspects(incident).Any(p => !incident.ConvoyCustodySuspectHandles.Contains(p.Handle))
                || HasPendingVictimInvestigation)
            {
                LogDebugTransition("POLICE_DISPATCH_COMPLETION_DEFERRED", incident.Id,
                    "Unresolved suspects or required investigation remain.");
                return;
            }
```

Changed code beginning at line 16675:

```csharp
            if (deferSceneCleanup || incident.PreparedActivityStarted)
```

Changed code beginning at line 16724:

```csharp
            {
                _ambientWorld.ReleasePreparedDispatchActivity(incident);
```

Changed code beginning at line 16727:

```csharp
            }
```

Changed code beginning at line 16737:

```csharp
                    .Where(ped => ped != null && ped.Exists()
                        && !incident.ConvoyCustodySuspectHandles.Contains(ped.Handle)))
```

Changed code beginning at line 16752:

```csharp
                        PreserveUntilPlayerLeavesArea = deadAftermath || !string.IsNullOrEmpty(incident.PreparedActivity)
```

Changed code beginning at line 16776:

```csharp
                        Expires = now.AddSeconds(DeferredCleanupMaximumSeconds),
                        PreserveUntilPlayerLeavesArea = !string.IsNullOrEmpty(incident.PreparedActivity)
```

Changed code beginning at line 16793:

```csharp
                        Expires = now.AddSeconds(DeferredCleanupMaximumSeconds),
                        PreserveUntilPlayerLeavesArea = !string.IsNullOrEmpty(incident.PreparedActivity)
```

Changed code beginning at line 16827:

```csharp
                        PreserveUntilPlayerLeavesArea = murderVictimAftermath || !string.IsNullOrEmpty(incident.PreparedActivity)
```

Changed code beginning at line 16868:

```csharp
                        Expires = now.AddSeconds(DeferredCleanupMaximumSeconds),
                        PreserveUntilPlayerLeavesArea = !string.IsNullOrEmpty(incident.PreparedActivity)
```

Changed code beginning at line 17053:

```csharp
            {
                _ambientWorld.ReleasePreparedDispatchActivity(incident);
```

Changed code beginning at line 17056:

```csharp
            }
```

Changed code beginning at line 17457:

```csharp
            if (IsAuthoredDrivingActivity(incident)) return true;
```

## LSPDDispatchEvent.cs

Changed code beginning at line 918:

```csharp
        internal string PreparedActivity { get; private set; }
```

Changed code beginning at line 944:

```csharp
        internal int AdditionalDeadVictims { get; private set; }
```

Changed code beginning at line 964:

```csharp
            if (!new[] { "standard", "interior_activity", "paired_meeting", "paired_exchange", "interior_store_custody", "interior_robbery_custody", "interior_attack_attempt", "interior_clerk_assault", "interior_clerk_snatch", "murder_victim_alley", "murder_victim_exterior", "murder_victim_interior" }
```

Changed code beginning at line 1044:

```csharp
                && !mappedRetailAttack
                && !string.Equals(sceneBehavior, "interior_activity", StringComparison.OrdinalIgnoreCase))
```

Changed code beginning at line 1104:

```csharp
                node, "minimumSceneParticipants", 0, 0, 5);
```

Changed code beginning at line 1107:

```csharp
                minimumSceneParticipants, 5);
```

Changed code beginning at line 1117:

```csharp
                && !new[] { "shop_clerk", "shop_customer", "gun_store_clerk", "tattoo_store_clerk", "bank_clerk", "ambient_civilian" }.Contains(
```

Changed code beginning at line 1122:

```csharp
            int additionalDeadVictims = IntegerInRange(node, "additionalDeadVictims", 0, 0, 4);
            if (additionalDeadVictims > minimumSceneParticipants || (additionalDeadVictims > 0 && !victimStartsDead))
                throw new InvalidDataException("Extra authored murder victims require reserved civilian slots and a murder scene.");
```

Changed code beginning at line 1155:

```csharp
            string preparedActivity = Optional(node, "preparedActivity", string.Empty);
            string[] supportedPreparedActivities = { "threat", "assault", "driver_assault", "shooting", "stalking", "camera", "intoxicated", "lying", "theft", "exchange", "gathering", "murder", "driving_fast", "driving_slow", "ramming", "carjacking" };
            if (!string.IsNullOrEmpty(preparedActivity)
                && !supportedPreparedActivities.Contains(preparedActivity, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsupported preparedActivity: " + preparedActivity);
            if ((preparedActivity == "exchange" || preparedActivity == "gathering") && minimumSuspects < 2)
                throw new InvalidDataException("Exchange/gathering needs at least two criminal participants.");
            if (preparedActivity.StartsWith("driving_", StringComparison.OrdinalIgnoreCase) || preparedActivity == "ramming")
                if (!Bool(node, "requiresVehicle", false) || !Bool(node, "mobile", false))
                    throw new InvalidDataException("A driving activity requires mobile vehicles.");
```

Changed code beginning at line 1401:

```csharp
            bool interiorActivity = string.Equals(sceneBehavior, "interior_activity", StringComparison.OrdinalIgnoreCase);
            if (interiorActivity && (string.IsNullOrEmpty(preparedActivity)
                || string.IsNullOrEmpty(requiredPhysicalType) || Bool(node, "requiresVehicle", false)))
                throw new InvalidDataException("An authored interior activity needs a physical site, activity, and foot actors.");
            bool inferredMappedInterior = interiorActivity || mappedInteriorRobberyCustody
```

Changed code beginning at line 1445:

```csharp
                    || maximumSuspects > 5
```

Changed code beginning at line 1457:

```csharp
                    "A mapped interior robbery requires one to five armed bank/store suspects, one matching clerk or civilian victim, compliance and resistance branches, no vehicle or exterior staging, and no paired animation.");
```

Changed code beginning at line 1462:

```csharp
                    || maximumSuspects > 5
```

Changed code beginning at line 1478:

```csharp
                    || maximumSuspects > 5
```

Changed code beginning at line 1521:

```csharp
                    || maximumSuspects > 5
```

Changed code beginning at line 1540:

```csharp
                && string.IsNullOrEmpty(preparedActivity)
```

Changed code beginning at line 1550:

```csharp
            if (!interiorActivity && !interiorStoreCustody && !mappedInteriorRobberyCustody
```

Changed code beginning at line 1576:

```csharp
                PreparedActivity = preparedActivity,
```

Changed code beginning at line 1601:

```csharp
                AdditionalDeadVictims = additionalDeadVictims,
```

Changed code beginning at line 1856:

```csharp
        internal string PreparedActivity { get; set; }
        internal bool PreparedActivityStarted { get; set; }
        internal bool PreparedActivityConfirmed { get; set; }
        internal bool PreparedActivityAudioReported { get; set; }
        internal HashSet<int> ResolvedSuspectHandles { get; private set; } = new HashSet<int>();
        internal Dictionary<int, DateTime> ActivityLastEvidenceAt { get; private set; } = new Dictionary<int, DateTime>();
        internal DateTime PreparedActivityStartedAt { get; set; }
        internal DateTime PreparedActivityNextSampleAt { get; set; }
        internal int PreparedActivitySamples { get; set; }
        internal bool RammingImpactConfirmed { get; set; }
        internal Dictionary<int, int> ActivityStages { get; private set; } = new Dictionary<int, int>();
        internal int PreparedActivityRecoveryCount { get; set; }
        internal Dictionary<int, Vector3> ActivitySamplePositions { get; private set; } = new Dictionary<int, Vector3>();
        internal Dictionary<int, DateTime> ActivityTaskIssuedAt { get; private set; } = new Dictionary<int, DateTime>();
        internal Dictionary<int, string> ActivityRoles { get; private set; } = new Dictionary<int, string>();
        internal HashSet<int> ActivityEvidenceHandles { get; private set; } = new HashSet<int>();
        internal List<Prop> ActivityProps { get; private set; } = new List<Prop>();
```

Changed code beginning at line 1939:

```csharp
        internal int AdditionalDeadVictims { get; set; }
        internal HashSet<int> InvestigatedVictimHandles { get; private set; } = new HashSet<int>();
```

Changed code beginning at line 2174:

```csharp
                PreparedActivity = definition.PreparedActivity,
```

Changed code beginning at line 2257:

```csharp
                AdditionalDeadVictims = definition.AdditionalDeadVictims,
```

## LSPDDispatchEvent.xml

Changed code beginning at line 58:

```xml
    <Branch id="Fleeing_Event" title="Individual suspect fleeing" phase="response_outcome" />
    <Branch id="Resisting_Event" title="Individual suspect resisting" phase="response_outcome" />
    <Branch id="Compliant_Event" title="Individual suspect compliant" phase="response_outcome" />
```

Changed code beginning at line 94:

```xml
    <Event id="reckless_driver" title="Reckless Driver Reported" type="reckless_driver" audioIncidentType="reckless_driver" briefing="A reckless driver is creating a hazard nearby." criminalProfileIds="reckless_commuter,street_driver" locationIds="downtown,strawberry,la_mesa,mirror_park,sandy_shores" branchIds="dispatch-reckless-driver,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="road_vehicle" vehicleActivity="moving_road" vehicleAssetTypes="escape_car,street_vehicle,crew_suv,cargo_van" requiresVehicle="true" mobile="true" startPursuitOnAccept="true" armed="false" severity="2" minimumCooldownSeconds="90" spawnDistance="110" selectionWeight="6" preparedActivity="driving_fast" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
```

Changed code beginning at line 97:

```xml
    <Event id="vehicle_pursuit" title="Vehicle Pursuit Requested" type="vehicle_pursuit" audioIncidentType="suspicious_vehicle" briefing="Units are requested for a fleeing vehicle." criminalProfileIds="street_driver,armed_offender" locationIds="downtown,del_perro,mirror_park,sandy_shores,paleto_bay" branchIds="dispatch-vehicle-pursuit-unreachable,dispatch-vehicle-pursuit-survives-stop,dispatch-vehicle-suspect-escalation,dispatch-player-cannot-pursue,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="road_vehicle" vehicleActivity="moving_road" vehicleAssetTypes="escape_car,street_vehicle,crew_suv,cargo_van" requiresVehicle="true" mobile="true" startPursuitOnAccept="true" armed="false" severity="3" minimumCooldownSeconds="120" spawnDistance="135" selectionWeight="6" preparedActivity="driving_fast" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="armed_suspect" title="Armed Suspect Reported" type="armed_suspect" audioIncidentType="armed_suspect" briefing="Possible armed suspects have been reported nearby." criminalProfileIds="armed_offender,organized_criminal" locationIds="mission_row,pillbox,strawberry,vespucci,la_mesa" branchIds="dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" severity="4" minimumCooldownSeconds="120" spawnDistance="95" preparedActivity="threat" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="shots_fired" title="Shots Fired Reported" type="shots_fired" audioIncidentType="shots_fired" briefing="Dispatch reports possible shots fired in your area." criminalProfileIds="armed_offender,organized_criminal" locationIds="downtown,strawberry,cypress,mirror_park,sandy_shores" branchIds="dispatch-team-backup-group,dispatch-foot-suspect-solo,dispatch-player-shoots-fleeing-suspect,dispatch-player-cannot-pursue,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" minimumSuspects="2" maximumSuspects="2" preArrivalActivity="group_shootout" suspectResponseBranches="comply,resist,flee" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="180" spawnDistance="100" preparedActivity="shooting" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
```

Changed code beginning at line 101:

```xml
    <Event id="store_robbery" title="Store Robbery Reported" type="store_robbery" audioIncidentType="store_robbery" briefing="A store robbery has been reported. Suspect details are limited." criminalProfileIds="gang_robber,armed_offender" locationIds="los_santos.vespucci.commercial,los_santos.vinewood.nightlife,los_santos.downtown.financial,los_santos.mirror_park.edge" branchIds="dispatch-store-robbery-fleeing-foot,dispatch-store-robbery-group-gunfire,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiresVehicle="true" mobile="true" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="3" preArrivalActivity="guard" suspectResponseBranches="comply,resist,flee" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" severity="4" minimumCooldownSeconds="150" spawnDistance="110" preparedActivity="driving_fast" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="store_robbery_interior_single" title="Store Robbery — Armed Suspect Inside" type="store_robbery" audioIncidentType="store_robbery" briefing="A clerk is being held at gunpoint inside a store. Respond and use caution." criminalProfileIds="gang_robber,armed_offender" locationIds="survey.site.20261002.000164" branchIds="dispatch-store-robbery-interior-custody,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" interiorLocationId="reference.lsr.v10547.5e39b4fa5543" sceneBehavior="interior_store_custody" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="240" spawnDistance="110" selectionWeight="8" preparedActivity="threat" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="bank_robbery" title="Bank Robbery Reported" type="bank_robbery" audioIncidentType="bank_robbery" briefing="A bank robbery is in progress. Respond when ready." criminalProfileIds="armed_robber,organized_criminal" locationIds="los_santos.downtown.financial" branchIds="dispatch-bank-robbery-escape,dispatch-bank-robbery-gunfire,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="financial" requiresVehicle="true" mobile="true" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="3" preArrivalActivity="guard" suspectResponseBranches="comply,resist,flee" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="210" spawnDistance="140" preparedActivity="driving_fast" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="kidnapping" title="Kidnapping In Progress" type="kidnapping" audioIncidentType="kidnapping" briefing="A victim is being forced away in a vehicle. Respond when ready." criminalProfileIds="organized_criminal,armed_offender" locationIds="mission_row,strawberry,vespucci,cypress,sandy_shores" branchIds="dispatch-alley-kidnapping,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" requiresVehicle="true" mobile="true" armed="true" suspectCount="2" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="210" spawnDistance="130" preparedActivity="driving_fast" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
```

Changed code beginning at line 106:

```xml
    <Event id="violent_disturbance" title="Violent Disturbance Reported" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="A violent disturbance is reported nearby. Use caution." criminalProfileIds="armed_offender,disturbance_subject" locationIds="mission_row,downtown,strawberry,la_mesa,cypress" branchIds="dispatch-armed-disturbance,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="180" spawnDistance="100" preparedActivity="assault" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="armed_business_disturbance" title="Armed Disturbance Near a Business" type="armed_suspect" audioIncidentType="armed_suspect" briefing="Dispatch reports an armed disturbance near a business. Approach with caution." criminalProfileIds="armed_offender,organized_criminal" locationIds="los_santos.vespucci.commercial,los_santos.vinewood.nightlife,los_santos.downtown.financial" branchIds="dispatch-armed-disturbance,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" severity="4" minimumCooldownSeconds="150" spawnDistance="90" preparedActivity="threat" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
```

Changed code beginning at line 109:

```xml
    <Event id="alley_drug_deal" title="Possible Drug Deal in an Alley" type="drug_activity" audioIncidentType="drug_activity" briefing="Dispatch reports several people meeting in an alley for possible drug activity." criminalProfileIds="disturbance_subject,nervous_suspect" locationIds="los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-alley-drug-deal,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="alley" sceneBehavior="paired_exchange" sceneAnimationDictionary="mp_common" sceneAnimationPrimaryClip="givetake1_a" sceneAnimationSecondaryClip="givetake1_b" minimumSuspects="2" maximumSuspects="5" suspectCount="2" severity="2" minimumCooldownSeconds="150" spawnDistance="90" preparedActivity="exchange" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="alley_criminal_meeting" title="Criminal Meeting Reported in an Alley" type="criminal_gathering" audioIncidentType="criminal_gathering" briefing="Dispatch reports a group meeting in an alley. Observe the group before taking action." criminalProfileIds="organized_criminal,disturbance_subject" locationIds="los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-alley-criminal-meeting,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="alley" sceneBehavior="paired_meeting" sceneAnimationDictionary="mp_ped_interaction" sceneAnimationPrimaryClip="handshake_guy_a" sceneAnimationSecondaryClip="handshake_guy_b" requiresVehicle="false" mobile="false" armed="false" suspectCount="2" minimumSuspects="2" maximumSuspects="5" severity="2" minimumCooldownSeconds="150" spawnDistance="90" preparedActivity="gathering" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="alley_money_armory_deal" title="Possible Money or Weapons Deal in an Alley" type="weapons_incident" audioIncidentType="weapons_incident" briefing="Dispatch reports a group meeting in an alley with a possible money or weapons exchange." criminalProfileIds="organized_criminal,armed_offender" locationIds="los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-alley-money-armory-deal,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="alley" sceneBehavior="paired_exchange" sceneAnimationDictionary="mp_common" sceneAnimationPrimaryClip="givetake1_a" sceneAnimationSecondaryClip="givetake1_b" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" minimumSuspects="2" maximumSuspects="5" preArrivalActivity="guard" suspectResponseBranches="comply,resist,flee" severity="4" minimumCooldownSeconds="180" spawnDistance="95" preparedActivity="exchange" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="road_vehicle_speed_chaos" title="Road Vehicle Speed Chaos" type="reckless_driver" audioIncidentType="reckless_driver" briefing="Dispatch reports a vehicle creating a speed hazard on a nearby road. Pursuit updates will follow its moving route." criminalProfileIds="reckless_commuter,street_driver" locationIds="blaine.chumash.coast,blaine.zancudo.supply_route,los_santos.banham.canyon,blaine.grand_senora.airstrip" branchIds="dispatch-road-vehicle-speed-chaos,dispatch-reckless-driver,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="road_vehicle" requiresVehicle="true" mobile="true" startPursuitOnAccept="true" armed="false" suspectCount="1" minimumSuspects="1" maximumSuspects="1" severity="3" minimumCooldownSeconds="150" spawnDistance="135" selectionWeight="6" preparedActivity="driving_fast" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" vehicleActivity="moving_road" vehicleAssetTypes="escape_car,street_vehicle,crew_suv,cargo_van" />
    <Event id="road_foot_shot_fired" title="Road Foot Suspect — Shots Fired" type="shots_fired" audioIncidentType="shots_fired" briefing="Dispatch reports shots fired and a suspect on foot near a road." criminalProfileIds="armed_offender,organized_criminal" locationIds="downtown,strawberry,vespucci,del_perro,sandy_shores,paleto_bay,blaine.chumash.coast,los_santos.banham.canyon" branchIds="dispatch-road-foot-shot-fired,dispatch-foot-suspect-solo,dispatch-player-cannot-pursue,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="road_foot" preArrivalActivity="active_shooter" suspectResponseBranches="comply,resist,flee" requiresVehicle="false" mobile="true" armed="true" suspectCount="1" severity="5" minimumCooldownSeconds="180" spawnDistance="100" preparedActivity="shooting" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="alley_shot_fired_single" title="Shots Fired — One Suspect in an Alley" type="shots_fired" audioIncidentType="shots_fired" briefing="Dispatch reports one armed suspect and shots fired in a narrow alley." criminalProfileIds="armed_offender,organized_criminal" locationIds="los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-alley-shot-fired-single,dispatch-foot-suspect-solo,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="alley" preArrivalActivity="active_shooter" suspectResponseBranches="comply,resist,flee" requiresVehicle="false" mobile="true" armed="true" suspectCount="1" severity="5" minimumCooldownSeconds="180" spawnDistance="95" preparedActivity="shooting" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="clothing_store_robbery" title="Clothing Store Robbery" type="store_robbery" audioIncidentType="store_robbery" briefing="Dispatch reports an armed robbery at a clothing store. Use caution and confirm the scene before acting." criminalProfileIds="gang_robber,armed_offender,armed_robber" locationIds="los_santos.vespucci.commercial,los_santos.vinewood.nightlife,los_santos.mirror_park.edge" branchIds="dispatch-store-robbery-fleeing-foot,dispatch-store-robbery-group-gunfire,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="ClothingStore" requiresVehicle="true" mobile="true" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="3" preArrivalActivity="guard" suspectResponseBranches="comply,resist,flee" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" severity="4" minimumCooldownSeconds="210" spawnDistance="120" preparedActivity="driving_fast" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="gun_store_robbery" title="Gun Store Group Jacking — Clerk and Customers Threatened Inside" type="store_robbery" audioIncidentType="store_robbery" briefing="Dispatch reports an armed group jacking inside a mapped gun store. The male gun clerk and customers are threatened; suspects may comply, fire, or flee through the mapped doorway." criminalProfileIds="armed_robber,organized_criminal,gang_robber" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge" branchIds="dispatch-armory-robbery-interior-attempt,dispatch-store-robbery-interior-custody,dispatch-store-robbery-group-gunfire,dispatch-store-robbery-fleeing-foot,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="GunStore" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" minimumSuspects="2" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="30" responseResistWeight="40" responseFleeWeight="30" victimPool="gun_store_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="2" maximumSceneParticipants="3" severity="5" minimumCooldownSeconds="360" spawnDistance="145" selectionWeight="8" preparedActivity="threat" />
    <Event id="liquor_store_robbery" title="Liquor Store Robbery" type="store_robbery" audioIncidentType="store_robbery" briefing="Dispatch reports an armed robbery at a liquor store." criminalProfileIds="gang_robber,armed_offender,armed_robber" locationIds="los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge" branchIds="dispatch-store-robbery-fleeing-foot,dispatch-store-robbery-group-gunfire,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="LiquorStore" requiresVehicle="true" mobile="true" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="3" preArrivalActivity="guard" suspectResponseBranches="comply,resist,flee" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" severity="4" minimumCooldownSeconds="210" spawnDistance="115" preparedActivity="driving_fast" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="convenience_store_robbery" title="Convenience Store Robbery" type="store_robbery" audioIncidentType="store_robbery" briefing="Dispatch reports an armed robbery at a convenience store." criminalProfileIds="gang_robber,armed_offender,armed_robber" locationIds="los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge" branchIds="dispatch-store-robbery-fleeing-foot,dispatch-store-robbery-group-gunfire,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="ConvenienceStore" requiresVehicle="true" mobile="true" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="3" preArrivalActivity="guard" suspectResponseBranches="comply,resist,flee" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" severity="4" minimumCooldownSeconds="210" spawnDistance="115" preparedActivity="driving_fast" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="bank_robbery_escape" title="Bank Robbery — Suspects Attempt to Escape" type="bank_robbery" audioIncidentType="bank_robbery" briefing="Dispatch reports a bank robbery with suspects preparing to escape." criminalProfileIds="armed_robber,organized_criminal" locationIds="los_santos.downtown.financial" branchIds="dispatch-bank-robbery-escape,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="financial" requiredPhysicalType="Bank" requiresVehicle="true" mobile="true" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="3" preArrivalActivity="guard" suspectResponseBranches="comply,resist,flee" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="240" spawnDistance="145" preparedActivity="driving_fast" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="bank_robbery_gunfire_group" title="Bank Robbery — Gunfire Reported" type="bank_robbery" audioIncidentType="bank_robbery" briefing="Dispatch reports gunfire during a bank robbery. Request Backup if needed." criminalProfileIds="armed_robber,organized_criminal" locationIds="los_santos.downtown.financial" branchIds="dispatch-bank-robbery-gunfire,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="financial" requiredPhysicalType="Bank" requiresVehicle="true" mobile="true" armed="true" suspectCount="2" minimumSuspects="2" maximumSuspects="3" preArrivalActivity="guard" suspectResponseBranches="comply,resist,flee" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="270" spawnDistance="145" preparedActivity="driving_fast" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="alley_group_shootout" title="Alley Shootout — Group Suspects" type="shots_fired" audioIncidentType="shots_fired" briefing="Dispatch reports armed suspects exchanging gunfire near an alley, with a civilian caught in the scene. The scene remains active while you respond." criminalProfileIds="armed_offender,organized_criminal,gang_robber" locationIds="los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-team-backup-group,dispatch-alley-shot-fired-single,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="alley" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" minimumSuspects="2" maximumSuspects="3" preArrivalActivity="group_shootout" suspectResponseBranches="comply,resist,flee" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="240" spawnDistance="105" preparedActivity="shooting" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="roadside_group_shootout" title="Roadside Shootout — Group Suspects" type="shots_fired" audioIncidentType="shots_fired" briefing="Dispatch reports armed suspects exchanging gunfire near a road, with a civilian caught in the scene. The scene remains active while you respond." criminalProfileIds="armed_offender,organized_criminal,gang_robber" locationIds="downtown,strawberry,vespucci,del_perro,sandy_shores,paleto_bay,blaine.chumash.coast,los_santos.banham.canyon" branchIds="dispatch-team-backup-group,dispatch-road-foot-shot-fired,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="road_foot" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" minimumSuspects="2" maximumSuspects="3" preArrivalActivity="group_shootout" suspectResponseBranches="comply,resist,flee" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="240" spawnDistance="110" preparedActivity="shooting" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="alley_attack_attempt" title="Attempted Attack in an Alley" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports an assault in progress in an alley. Respond and protect the victim." criminalProfileIds="armed_offender,disturbance_subject,gang_robber" locationIds="los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-armed-disturbance,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="alley" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="3" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="30" responseResistWeight="35" responseFleeWeight="35" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="210" spawnDistance="100" preparedActivity="threat" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" />
    <Event id="roadside_attack_attempt" title="Attempted Attack Near a Road" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports an assault in progress near a road. Respond and protect the victim." criminalProfileIds="armed_offender,disturbance_subject,gang_robber" locationIds="downtown,strawberry,vespucci,del_perro,sandy_shores,paleto_bay,blaine.chumash.coast,los_santos.banham.canyon" branchIds="dispatch-armed-disturbance,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="road_foot" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="3" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="30" responseResistWeight="35" responseFleeWeight="35" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="210" spawnDistance="105" preparedActivity="threat" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" />
    <Event id="roadside_vehicle_kidnapping" title="Vehicle Kidnapping in Progress" type="kidnapping" audioIncidentType="kidnapping" briefing="Dispatch reports a victim being forced away in a vehicle near a road." criminalProfileIds="organized_criminal,armed_offender" locationIds="mission_row,strawberry,vespucci,cypress,sandy_shores,paleto_bay" branchIds="dispatch-road-snatch-victim,dispatch-alley-kidnapping,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="road_vehicle" requiresVehicle="true" mobile="true" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="2" preArrivalActivity="victim_threat" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="240" spawnDistance="140" selectionWeight="6" preparedActivity="driving_fast" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="roadside_vehicle_kidnapping_attempt" title="Attempted Vehicle Kidnapping Near a Road" type="kidnapping" audioIncidentType="kidnapping" briefing="Dispatch reports an attempted vehicle kidnapping with the victim still present." criminalProfileIds="organized_criminal,armed_offender" locationIds="mission_row,pillbox,strawberry,vespucci,la_mesa,cypress,sandy_shores,paleto_bay" branchIds="dispatch-kidnapping-attempt,dispatch-road-snatch-victim,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="road_vehicle" requiresVehicle="true" mobile="true" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="2" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="25" responseFleeWeight="50" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="270" spawnDistance="145" selectionWeight="6" preparedActivity="driving_fast" />
```

Changed code beginning at line 128:

```xml
    <Event id="vehicle_shootout_pursuit" title="Armed Vehicle Pursuit — Shots Fired" type="vehicle_pursuit" audioIncidentType="suspicious_vehicle" briefing="Dispatch reports shots fired from a moving vehicle. Follow the suspect marker and request Backup if needed." criminalProfileIds="armed_offender,organized_criminal" locationIds="mission_row,downtown,del_perro,la_mesa,sandy_shores,paleto_bay" branchIds="dispatch-vehicle-suspect-escalation,dispatch-vehicle-pursuit-unreachable,dispatch-vehicle-pursuit-survives-stop,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="road_vehicle" vehicleActivity="moving_road" vehicleAssetTypes="escape_car,street_vehicle,crew_suv,cargo_van" requiresVehicle="true" mobile="true" startPursuitOnAccept="true" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="1" severity="5" minimumCooldownSeconds="240" spawnDistance="145" selectionWeight="6" preparedActivity="driving_fast" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="group_two_vehicle_pursuit" title="Two-Vehicle Group Pursuit" type="vehicle_pursuit" audioIncidentType="suspicious_vehicle" briefing="Dispatch reports two related vehicles fleeing together. Follow the moving pursuit marker and coordinate Backup for a safe stop." criminalProfileIds="street_driver,reckless_commuter,nervous_suspect" locationIds="mission_row,downtown,strawberry,vespucci,la_mesa,sandy_shores,paleto_bay" branchIds="dispatch-team-backup-group,dispatch-vehicle-pursuit-unreachable,dispatch-vehicle-pursuit-survives-stop,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="road_vehicle" vehicleActivity="moving_road" vehicleAssetTypes="escape_car,street_vehicle,crew_suv,cargo_van" requiresVehicle="true" mobile="true" startPursuitOnAccept="true" armed="false" suspectCount="2" minimumSuspects="2" maximumSuspects="2" minimumSuspectVehicles="2" maximumSuspectVehicles="2" preArrivalActivity="guard" suspectResponseBranches="comply,resist,flee" responseComplyWeight="20" responseResistWeight="15" responseFleeWeight="65" selectionWeight="6" severity="4" minimumCooldownSeconds="300" spawnDistance="145" preparedActivity="driving_fast" />
    <Event id="alley_murder_victim_scene" title="Alley Murder — Suspect Near Victim" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports a suspect near a murder victim in an alley. The scene is still active; investigate the victim and resolve the suspect." criminalProfileIds="armed_offender,organized_criminal" locationIds="los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-alley-murder-victim,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="alley" sceneBehavior="murder_victim_alley" victimPool="ambient_civilian" victimStartsDead="true" minimumVictims="1" maximumVictims="1" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="murder_scene" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="25" responseFleeWeight="40" selectionWeight="1" severity="5" minimumCooldownSeconds="300" spawnDistance="105" preparedActivity="murder" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" />
    <Event id="store_robbery_mapped_interior_attempt" title="Armed Robbery Inside a Cataloged Store" type="store_robbery" audioIncidentType="store_robbery" briefing="A clerk is reported held inside a nearby store. Enter through the mapped public entrance and resolve the threat." criminalProfileIds="gang_robber,armed_offender,armed_robber" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,sandy_shores,paleto_bay,los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge,los_santos.vinewood.nightlife" branchIds="dispatch-store-robbery-interior-custody,dispatch-store-robbery-fleeing-foot,dispatch-store-robbery-group-gunfire,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="35" responseFleeWeight="30" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" selectionWeight="8" severity="5" minimumCooldownSeconds="300" spawnDistance="145" preparedActivity="threat" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" />
    <Event id="clothing_store_mapped_interior_attempt" title="Clothing Store Robbery — Suspect Inside" type="store_robbery" audioIncidentType="store_robbery" briefing="An armed suspect is reported inside a clothing store with a clerk present." criminalProfileIds="gang_robber,armed_offender,armed_robber" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,los_santos.vespucci.commercial,los_santos.vinewood.nightlife,los_santos.mirror_park.edge" branchIds="dispatch-store-robbery-interior-custody,dispatch-store-robbery-fleeing-foot,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="ClothingStore" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="35" responseFleeWeight="30" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="330" spawnDistance="145" selectionWeight="8" preparedActivity="threat" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" />
    <Event id="gun_store_mapped_interior_attempt" title="Gun Store Robbery — One Suspect Holds the Male Clerk" type="store_robbery" audioIncidentType="store_robbery" briefing="An armed suspect is reported holding the male gun-store clerk inside. Enter through the mapped public doorway; the suspect may comply, resist, or flee on foot." criminalProfileIds="armed_robber,organized_criminal,gang_robber" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge" branchIds="dispatch-armory-robbery-interior-attempt,dispatch-store-robbery-interior-custody,dispatch-store-robbery-group-gunfire,dispatch-store-robbery-fleeing-foot,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="GunStore" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="1" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="35" responseFleeWeight="30" victimPool="gun_store_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="1" maximumSceneParticipants="2" severity="5" minimumCooldownSeconds="360" spawnDistance="145" selectionWeight="8" preparedActivity="threat" />
    <Event id="gun_store_clerk_armed_assault_mapped_interior" title="Armed Assault on Gun Store Clerk" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports an armed assault in progress inside a mapped gun store. The male clerk is threatened; respond through the public entrance and resolve the suspect's reaction." criminalProfileIds="armed_offender,organized_criminal" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge" branchIds="dispatch-interior-attack-attempt,dispatch-attempted-murder,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="GunStore" sceneBehavior="interior_attack_attempt" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="gun_store_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="0" maximumSceneParticipants="2" selectionWeight="8" severity="5" minimumCooldownSeconds="330" spawnDistance="145" preparedActivity="threat" />
    <Event id="gun_store_customer_armed_assault_mapped_interior" title="Armed Assault on Customer — Gun Clerk Present" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports an armed assault on a customer inside a mapped gun store. The male clerk is also inside; enter through the mapped doorway and respond to compliance, resistance, or flight." criminalProfileIds="armed_offender,organized_criminal" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge" branchIds="dispatch-interior-attack-attempt,dispatch-attempted-murder,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="GunStore" sceneBehavior="interior_attack_attempt" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="shop_customer" minimumVictims="1" maximumVictims="1" sceneParticipantPool="gun_store_clerk" minimumSceneParticipants="1" maximumSceneParticipants="1" selectionWeight="8" severity="5" minimumCooldownSeconds="330" spawnDistance="145" preparedActivity="threat" />
```

Changed code beginning at line 138:

```xml
    <Event id="gun_store_clerk_murder_mapped_interior" title="Gun Store Murder — Male Clerk Dead, Suspect Nearby" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports a murder inside a mapped gun store with a suspect still nearby. The male clerk is dead; enter through the mapped doorway, investigate, and resolve the suspect." criminalProfileIds="armed_offender,organized_criminal,gang_robber" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge" branchIds="dispatch-interior-murder-victim,dispatch-single-suspect-escalation,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="GunStore" sceneBehavior="murder_victim_interior" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="murder_scene" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="gun_store_clerk" victimStartsDead="true" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="0" maximumSceneParticipants="2" selectionWeight="8" severity="5" minimumCooldownSeconds="360" spawnDistance="145" preparedActivity="murder" />
    <Event id="gun_store_customer_murder_mapped_interior" title="Gun Store Murder — Customer Dead, Male Clerk Present" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports a murder inside a mapped gun store. A customer is dead and the male clerk remains inside; enter through the mapped doorway, investigate, and resolve the nearby suspect." criminalProfileIds="armed_offender,organized_criminal,gang_robber" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge" branchIds="dispatch-interior-murder-victim,dispatch-single-suspect-escalation,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="GunStore" sceneBehavior="murder_victim_interior" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="murder_scene" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="shop_customer" victimStartsDead="true" minimumVictims="1" maximumVictims="1" sceneParticipantPool="gun_store_clerk" minimumSceneParticipants="1" maximumSceneParticipants="1" selectionWeight="8" severity="5" minimumCooldownSeconds="360" spawnDistance="145" preparedActivity="murder" />
    <Event id="bank_robbery_mapped_interior_attempt" title="Bank Robbery — Banker and Customers Threatened" type="bank_robbery" audioIncidentType="bank_robbery" briefing="An armed bank robbery is reported inside a cataloged branch. A banker and customers are threatened while the suspects remain inside; approach the mapped entrance and confirm the scene." criminalProfileIds="armed_robber" locationIds="downtown,pillbox,mission_row,los_santos.downtown.financial" branchIds="dispatch-bank-robbery-interior-attempt,dispatch-bank-robbery-gunfire,dispatch-bank-robbery-escape,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="financial" requiredPhysicalType="Bank" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="35" responseFleeWeight="30" victimPool="bank_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="1" maximumSceneParticipants="2" severity="5" minimumCooldownSeconds="360" spawnDistance="145" selectionWeight="8" preparedActivity="threat" />
    <Event id="interior_attack_attempt" title="Attempted Attack Inside an Accessible Building" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports an armed attack in progress inside a nearby accessible building. The victim remains alive; enter through its mapped entrance." criminalProfileIds="armed_offender,disturbance_subject,organized_criminal" locationIds="mission_row,pillbox,downtown,strawberry,vespucci,del_perro,la_mesa,cypress,vinewood,mirror_park,sandy_shores,paleto_bay" branchIds="dispatch-interior-attack-attempt,dispatch-team-backup-group,dispatch-attempted-murder,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" sceneBehavior="interior_attack_attempt" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="145" selectionWeight="8" preparedActivity="threat" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" />
    <Event id="barber_shop_clerk_assault_mapped_interior" title="Clerk Assault Inside a Barber Shop" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports an assault in progress inside a mapped barber shop. A clerk is present; enter through the public doorway and resolve the attack." criminalProfileIds="disturbance_subject" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,sandy_shores,paleto_bay" branchIds="dispatch-interior-clerk-assault,dispatch-team-backup-group,dispatch-interior-attack-attempt,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="BarberShop" sceneBehavior="interior_clerk_assault" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="false" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_attack" suspectResponseBranches="comply,resist,flee" responseComplyWeight="30" responseResistWeight="35" responseFleeWeight="35" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" selectionWeight="8" severity="5" minimumCooldownSeconds="300" spawnDistance="145" preparedActivity="assault" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" />
```

Changed code beginning at line 144:

```xml
    <Event id="interior_murder_victim_scene" title="Murder Scene Inside an Accessible Building" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports a murder victim inside an accessible building with a suspect still nearby. Enter through the mapped doorway, investigate the victim, and resolve the suspect." criminalProfileIds="armed_offender,organized_criminal,gang_robber" locationIds="mission_row,pillbox,downtown,strawberry,vespucci,del_perro,la_mesa,cypress,vinewood,mirror_park,sandy_shores,paleto_bay,los_santos.east_vinewood.hills,los_santos.mirror_park.edge,los_santos.pacific_bluffs.estate" branchIds="dispatch-interior-murder-victim,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" sceneBehavior="murder_victim_interior" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="murder_scene" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="ambient_civilian" victimStartsDead="true" minimumVictims="1" maximumVictims="1" selectionWeight="8" severity="5" minimumCooldownSeconds="330" spawnDistance="145" preparedActivity="murder" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" />
    <Event id="residential_burglary_occupied_home_attempt" title="Occupied Home Break-In — Resident Threatened" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports a break-in at an occupied home with a resident threatened inside. Respond and enter through the mapped doorway." criminalProfileIds="armed_offender,organized_criminal,gang_robber" locationIds="mirror_park,sandy_shores,los_santos.east_vinewood.hills,los_santos.mirror_park.edge,los_santos.pacific_bluffs.estate" branchIds="dispatch-residential-burglary-attempt,dispatch-interior-attack-attempt,dispatch-team-backup-group,Resisting_Event,Compliant_Event" locationContext="residential" sceneBehavior="interior_attack_attempt" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist" responseComplyWeight="30" responseResistWeight="70" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="330" spawnDistance="145" selectionWeight="8" preparedActivity="threat" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" responseFleeWeight="0" />
    <Event id="alley_attempted_murder" title="Attempted Murder in an Alley" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports a victim under armed threat in an alley. The victim is alive and the incident remains active." criminalProfileIds="armed_offender,organized_criminal,gang_robber" locationIds="mission_row,strawberry,la_mesa,cypress,los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-attempted-murder,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="alley" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="3" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="110" preparedActivity="threat" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" />
    <Event id="roadside_attempted_murder" title="Attempted Murder Near a Road" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports an armed attempt on a person's life near a road. The victim is still alive; arrive and investigate." criminalProfileIds="armed_offender,organized_criminal,gang_robber" locationIds="mission_row,pillbox,downtown,strawberry,vespucci,del_perro,la_mesa,cypress,sandy_shores,paleto_bay,blaine.chumash.coast,los_santos.banham.canyon" branchIds="dispatch-attempted-murder,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="road_foot" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="3" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="115" preparedActivity="threat" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" />
    <Event id="alley_kidnapping_attempt" title="Kidnapping Attempt in an Alley" type="kidnapping" audioIncidentType="kidnapping" briefing="Dispatch reports an armed kidnapping attempt in an alley. The victim is still present and the scene remains active." criminalProfileIds="organized_criminal,armed_offender" locationIds="mission_row,strawberry,la_mesa,cypress,los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-kidnapping-attempt,dispatch-alley-kidnapping,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="alley" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="2" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimResponseBranches="attempt_escape,remain_threatened" victimAttemptEscapeWeight="60" victimRemainThreatenedWeight="40" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="110" preparedActivity="threat" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" />
    <Event id="convenience_store_mapped_interior_robbery" title="Convenience Store Robbery — Clerk and Customers Threatened" type="store_robbery" audioIncidentType="store_robbery" briefing="Dispatch reports an armed robbery inside a cataloged convenience store. The clerk and customers are being threatened; enter through the mapped public doorway and respond to the active suspects." criminalProfileIds="gang_robber,armed_offender,armed_robber" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,sandy_shores,paleto_bay,los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge,los_santos.vinewood.nightlife" branchIds="dispatch-store-robbery-interior-custody,dispatch-store-robbery-fleeing-foot,dispatch-store-robbery-group-gunfire,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="ConvenienceStore" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="35" responseFleeWeight="30" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="2" maximumSceneParticipants="3" selectionWeight="8" severity="5" minimumCooldownSeconds="300" spawnDistance="145" preparedActivity="threat" />
    <Event id="convenience_store_mapped_interior_jacking" title="Convenience Store Jacking — Armed Group and Customers Inside" type="store_robbery" audioIncidentType="store_robbery" briefing="Dispatch reports a group armed jacking inside a cataloged convenience store. A clerk and several customers remain inside; suspects may comply, resist with gunfire, or flee on foot through the mapped doors." criminalProfileIds="armed_robber,organized_criminal,gang_robber" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,sandy_shores,paleto_bay,los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge,los_santos.vinewood.nightlife" branchIds="dispatch-store-robbery-interior-custody,dispatch-store-robbery-group-gunfire,dispatch-store-robbery-fleeing-foot,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="ConvenienceStore" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" minimumSuspects="2" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="30" responseResistWeight="40" responseFleeWeight="30" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="2" maximumSceneParticipants="3" selectionWeight="8" severity="5" minimumCooldownSeconds="330" spawnDistance="145" preparedActivity="threat" />
    <Event id="convenience_store_clerk_assault_mapped_interior" title="Clerk Assault Inside a Convenience Store" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports an unarmed assault in progress inside a mapped convenience store. The clerk is being attacked; nearby customers may be present and react as the Player enters." criminalProfileIds="disturbance_subject" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,sandy_shores,paleto_bay,los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge,los_santos.vinewood.nightlife" branchIds="dispatch-convenience-liquor-clerk-assault,dispatch-team-backup-group,dispatch-interior-attack-attempt,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="ConvenienceStore" sceneBehavior="interior_clerk_assault" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="false" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_attack" suspectResponseBranches="comply,resist,flee" responseComplyWeight="30" responseResistWeight="35" responseFleeWeight="35" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="0" maximumSceneParticipants="2" selectionWeight="8" severity="5" minimumCooldownSeconds="300" spawnDistance="145" preparedActivity="assault" />
    <Event id="convenience_store_customer_assault_mapped_interior" title="Customer Assault Inside a Convenience Store" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports an unarmed assault inside a mapped convenience store. A customer is the victim and the clerk is present at the scene; the suspect may comply, resist, or flee on foot." criminalProfileIds="disturbance_subject" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,sandy_shores,paleto_bay,los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge,los_santos.vinewood.nightlife" branchIds="dispatch-convenience-liquor-customer-assault,dispatch-team-backup-group,dispatch-interior-attack-attempt,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="ConvenienceStore" sceneBehavior="interior_clerk_assault" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="false" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_attack" suspectResponseBranches="comply,resist,flee" responseComplyWeight="30" responseResistWeight="35" responseFleeWeight="35" victimPool="shop_customer" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_clerk" minimumSceneParticipants="1" maximumSceneParticipants="1" selectionWeight="8" severity="5" minimumCooldownSeconds="300" spawnDistance="145" preparedActivity="assault" />
```

Changed code beginning at line 155:

```xml
    <Event id="convenience_store_murder_mapped_interior" title="Murder Scene Inside a Convenience Store" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports a murder victim inside a mapped convenience store with the suspect still nearby. The clerk is authored dead before offer; any customers present remain separate scene participants." criminalProfileIds="armed_offender,organized_criminal,gang_robber" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,sandy_shores,paleto_bay,los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge,los_santos.vinewood.nightlife" branchIds="dispatch-interior-murder-victim,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="ConvenienceStore" sceneBehavior="murder_victim_interior" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="murder_scene" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="shop_clerk" victimStartsDead="true" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="0" maximumSceneParticipants="2" selectionWeight="8" severity="5" minimumCooldownSeconds="330" spawnDistance="145" preparedActivity="murder" />
    <Event id="liquor_store_mapped_interior_robbery" title="Liquor Store Robbery — Clerk and Customers Threatened" type="store_robbery" audioIncidentType="store_robbery" briefing="Dispatch reports an armed robbery inside a cataloged liquor store. The clerk and customers are being threatened; enter through the mapped public doorway and respond to the active suspects." criminalProfileIds="gang_robber,armed_offender,armed_robber" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,sandy_shores,paleto_bay,los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge,los_santos.vinewood.nightlife" branchIds="dispatch-store-robbery-interior-custody,dispatch-store-robbery-fleeing-foot,dispatch-store-robbery-group-gunfire,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="LiquorStore" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="35" responseFleeWeight="30" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="2" maximumSceneParticipants="3" selectionWeight="8" severity="5" minimumCooldownSeconds="300" spawnDistance="145" preparedActivity="threat" />
    <Event id="liquor_store_mapped_interior_jacking" title="Liquor Store Jacking — Armed Group and Customers Inside" type="store_robbery" audioIncidentType="store_robbery" briefing="Dispatch reports a group armed jacking inside a cataloged liquor store. A clerk and several customers remain inside; suspects may comply, resist with gunfire, or flee on foot through the mapped doors." criminalProfileIds="armed_robber,organized_criminal,gang_robber" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,sandy_shores,paleto_bay,los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge,los_santos.vinewood.nightlife" branchIds="dispatch-store-robbery-interior-custody,dispatch-store-robbery-group-gunfire,dispatch-store-robbery-fleeing-foot,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="LiquorStore" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" minimumSuspects="2" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="30" responseResistWeight="40" responseFleeWeight="30" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="2" maximumSceneParticipants="3" selectionWeight="8" severity="5" minimumCooldownSeconds="330" spawnDistance="145" preparedActivity="threat" />
    <Event id="liquor_store_clerk_assault_mapped_interior" title="Clerk Assault Inside a Liquor Store" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports an unarmed assault in progress inside a mapped liquor store. The clerk is being attacked; nearby customers may be present and react as the Player enters." criminalProfileIds="disturbance_subject" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,sandy_shores,paleto_bay,los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge,los_santos.vinewood.nightlife" branchIds="dispatch-convenience-liquor-clerk-assault,dispatch-team-backup-group,dispatch-interior-attack-attempt,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="LiquorStore" sceneBehavior="interior_clerk_assault" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="false" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_attack" suspectResponseBranches="comply,resist,flee" responseComplyWeight="30" responseResistWeight="35" responseFleeWeight="35" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="0" maximumSceneParticipants="2" selectionWeight="8" severity="5" minimumCooldownSeconds="300" spawnDistance="145" preparedActivity="assault" />
    <Event id="liquor_store_customer_assault_mapped_interior" title="Customer Assault Inside a Liquor Store" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports an unarmed assault inside a mapped liquor store. A customer is the victim and the clerk is present at the scene; the suspect may comply, resist, or flee on foot." criminalProfileIds="disturbance_subject" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,sandy_shores,paleto_bay,los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge,los_santos.vinewood.nightlife" branchIds="dispatch-convenience-liquor-customer-assault,dispatch-team-backup-group,dispatch-interior-attack-attempt,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="LiquorStore" sceneBehavior="interior_clerk_assault" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="false" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_attack" suspectResponseBranches="comply,resist,flee" responseComplyWeight="30" responseResistWeight="35" responseFleeWeight="35" victimPool="shop_customer" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_clerk" minimumSceneParticipants="1" maximumSceneParticipants="1" selectionWeight="8" severity="5" minimumCooldownSeconds="300" spawnDistance="145" preparedActivity="assault" />
```

Changed code beginning at line 162:

```xml
    <Event id="liquor_store_murder_mapped_interior" title="Murder Scene Inside a Liquor Store" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports a murder victim inside a mapped liquor store with the suspect still nearby. The clerk is authored dead before offer; any customers present remain separate scene participants." criminalProfileIds="armed_offender,organized_criminal,gang_robber" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,sandy_shores,paleto_bay,los_santos.vespucci.commercial,los_santos.downtown.financial,los_santos.mirror_park.edge,los_santos.vinewood.nightlife" branchIds="dispatch-interior-murder-victim,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="LiquorStore" sceneBehavior="murder_victim_interior" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="murder_scene" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="shop_clerk" victimStartsDead="true" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="0" maximumSceneParticipants="2" selectionWeight="8" severity="5" minimumCooldownSeconds="330" spawnDistance="145" preparedActivity="murder" />
      <Event id="tattoo_store_mapped_interior_jacking" title="Tattoo Shop Armed Jacking - Clerk and Customer Threatened" type="store_robbery" audioIncidentType="store_robbery" briefing="Dispatch reports an armed jacking inside a mapped tattoo shop. The male tattoo clerk and a customer are threatened; suspects may comply, resist, or flee through the mapped doorway." criminalProfileIds="armed_robber,organized_criminal,gang_robber" branchIds="dispatch-armory-robbery-interior-attempt,dispatch-store-robbery-interior-custody,dispatch-store-robbery-group-gunfire,dispatch-store-robbery-fleeing-foot,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationIds="mirror_park,los_santos.mirror_park.edge,paleto_bay" locationContext="commercial" requiredPhysicalType="TattooShop" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="30" responseResistWeight="40" responseFleeWeight="30" victimPool="tattoo_store_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="1" maximumSceneParticipants="1" severity="5" minimumCooldownSeconds="360" spawnDistance="145" selectionWeight="8" preparedActivity="threat" />
    <Event id="tattoo_store_mapped_interior_gunfire" title="Tattoo Shop Robbery - Gunfire and Two Suspects Inside" type="store_robbery" audioIncidentType="shots_fired" briefing="Dispatch reports shots fired during an armed jacking inside a mapped tattoo shop. The male clerk and a customer are inside with two suspects; enter through the mapped doorway and respond to the active threat." criminalProfileIds="armed_robber,organized_criminal,gang_robber" branchIds="dispatch-armory-robbery-interior-attempt,dispatch-store-robbery-interior-custody,dispatch-store-robbery-group-gunfire,dispatch-store-robbery-fleeing-foot,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationIds="mirror_park,los_santos.mirror_park.edge,paleto_bay" locationContext="commercial" requiredPhysicalType="TattooShop" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" minimumSuspects="2" maximumSuspects="2" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="40" responseFleeWeight="35" victimPool="tattoo_store_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="1" maximumSceneParticipants="1" severity="5" minimumCooldownSeconds="360" spawnDistance="145" selectionWeight="8" preparedActivity="threat" />
    <Event id="tattoo_store_clerk_armed_assault_mapped_interior" title="Armed Assault on Tattoo Clerk" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports an armed assault on the male clerk inside a mapped tattoo shop. A customer may also be inside; the suspect may comply, resist, or flee through the doorway." criminalProfileIds="armed_offender,organized_criminal" branchIds="dispatch-interior-attack-attempt,dispatch-attempted-murder,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationIds="mirror_park,los_santos.mirror_park.edge,paleto_bay" locationContext="commercial" requiredPhysicalType="TattooShop" sceneBehavior="interior_attack_attempt" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="tattoo_store_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="0" maximumSceneParticipants="1" severity="5" minimumCooldownSeconds="330" spawnDistance="145" selectionWeight="8" preparedActivity="threat" />
    <Event id="tattoo_store_customer_armed_assault_mapped_interior" title="Armed Assault on Tattoo Customer - Clerk Present" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports an armed assault on a customer inside a mapped tattoo shop. The male tattoo clerk remains inside; enter through the mapped doorway and respond to compliance, resistance, or flight." criminalProfileIds="armed_offender,organized_criminal" branchIds="dispatch-interior-attack-attempt,dispatch-attempted-murder,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationIds="mirror_park,los_santos.mirror_park.edge,paleto_bay" locationContext="commercial" requiredPhysicalType="TattooShop" sceneBehavior="interior_attack_attempt" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="shop_customer" minimumVictims="1" maximumVictims="1" sceneParticipantPool="tattoo_store_clerk" minimumSceneParticipants="1" maximumSceneParticipants="1" severity="5" minimumCooldownSeconds="330" spawnDistance="145" selectionWeight="8" preparedActivity="threat" />
    <Event id="tattoo_store_clerk_assault_mapped_interior" title="Assault on Tattoo Clerk" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports an assault in progress on the male clerk inside a mapped tattoo shop. The suspect is unarmed and may comply, resist, or flee through the mapped doorway." criminalProfileIds="disturbance_subject" branchIds="dispatch-interior-clerk-assault,dispatch-team-backup-group,dispatch-interior-attack-attempt,Fleeing_Event,Resisting_Event,Compliant_Event" locationIds="mirror_park,los_santos.mirror_park.edge,paleto_bay" locationContext="commercial" requiredPhysicalType="TattooShop" sceneBehavior="interior_clerk_assault" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="false" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_attack" suspectResponseBranches="comply,resist,flee" responseComplyWeight="30" responseResistWeight="35" responseFleeWeight="35" victimPool="tattoo_store_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="0" maximumSceneParticipants="1" severity="5" minimumCooldownSeconds="300" spawnDistance="140" selectionWeight="8" preparedActivity="assault" />
    <Event id="tattoo_store_customer_assault_mapped_interior" title="Assault on Tattoo Customer - Clerk Present" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports an unarmed assault on a customer inside a mapped tattoo shop. The male clerk is present; respond through the mapped entrance and resolve the suspect reaction." criminalProfileIds="disturbance_subject" branchIds="dispatch-interior-clerk-assault,dispatch-team-backup-group,dispatch-interior-attack-attempt,Fleeing_Event,Resisting_Event,Compliant_Event" locationIds="mirror_park,los_santos.mirror_park.edge,paleto_bay" locationContext="commercial" requiredPhysicalType="TattooShop" sceneBehavior="interior_clerk_assault" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="false" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_attack" suspectResponseBranches="comply,resist,flee" responseComplyWeight="30" responseResistWeight="35" responseFleeWeight="35" victimPool="shop_customer" minimumVictims="1" maximumVictims="1" sceneParticipantPool="tattoo_store_clerk" minimumSceneParticipants="1" maximumSceneParticipants="1" severity="5" minimumCooldownSeconds="300" spawnDistance="140" selectionWeight="8" preparedActivity="assault" />
```

Changed code beginning at line 171:

```xml
    <Event id="tattoo_store_clerk_murder_mapped_interior" title="Tattoo Shop Murder - Clerk Dead, Suspect Nearby" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports a murder inside a mapped tattoo shop. The male clerk is dead and a suspect remains nearby; enter through the mapped doorway and respond to the suspect." criminalProfileIds="armed_offender,organized_criminal,gang_robber" branchIds="dispatch-interior-murder-victim,dispatch-single-suspect-escalation,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationIds="mirror_park,los_santos.mirror_park.edge,paleto_bay" locationContext="commercial" requiredPhysicalType="TattooShop" sceneBehavior="murder_victim_interior" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="murder_scene" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="tattoo_store_clerk" victimStartsDead="true" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="0" maximumSceneParticipants="1" severity="5" minimumCooldownSeconds="360" spawnDistance="145" selectionWeight="8" preparedActivity="murder" />
    <Event id="tattoo_store_customer_murder_mapped_interior" title="Tattoo Shop Murder - Customer Dead, Clerk Present" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports a murder inside a mapped tattoo shop. A customer is dead and the male clerk remains present; enter through the mapped doorway and respond to the suspect." criminalProfileIds="armed_offender,organized_criminal,gang_robber" branchIds="dispatch-interior-murder-victim,dispatch-single-suspect-escalation,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationIds="mirror_park,los_santos.mirror_park.edge,paleto_bay" locationContext="commercial" requiredPhysicalType="TattooShop" sceneBehavior="murder_victim_interior" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="murder_scene" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="shop_customer" victimStartsDead="true" minimumVictims="1" maximumVictims="1" sceneParticipantPool="tattoo_store_clerk" minimumSceneParticipants="1" maximumSceneParticipants="1" severity="5" minimumCooldownSeconds="360" spawnDistance="145" selectionWeight="8" preparedActivity="murder" />
    <Event id="bank_robbery_mapped_interior_group_heist" title="Bank Heist — Armed Group Holds the Banker" type="bank_robbery" audioIncidentType="bank_robbery" briefing="Dispatch reports a group bank heist inside a cataloged branch. Armed robber-profile suspects threaten the banker while customers remain inside; request Backup if needed and enter through the mapped public doorway." criminalProfileIds="armed_robber" locationIds="downtown,pillbox,mission_row,los_santos.downtown.financial" branchIds="dispatch-bank-robbery-interior-attempt,dispatch-bank-robbery-escape,dispatch-bank-robbery-gunfire,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="financial" requiredPhysicalType="Bank" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" minimumSuspects="2" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="30" responseResistWeight="40" responseFleeWeight="30" victimPool="bank_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="1" maximumSceneParticipants="2" severity="5" minimumCooldownSeconds="390" spawnDistance="145" selectionWeight="8" preparedActivity="threat" />
    <Event id="bank_robbery_mapped_interior_gunfire_group" title="Bank Heist — Group Gunfire Inside the Branch" type="bank_robbery" audioIncidentType="shots_fired" briefing="Dispatch reports gunfire during an armed group heist inside a cataloged bank. The banker and customers are inside with two or three suspects; enter through the mapped doorway and request Backup if you need support." criminalProfileIds="armed_robber" locationIds="downtown,pillbox,mission_row,los_santos.downtown.financial" branchIds="dispatch-bank-robbery-gunfire,dispatch-bank-robbery-escape,dispatch-bank-robbery-interior-attempt,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="financial" requiredPhysicalType="Bank" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" minimumSuspects="2" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="20" responseResistWeight="45" responseFleeWeight="35" victimPool="bank_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="1" maximumSceneParticipants="2" severity="5" minimumCooldownSeconds="420" spawnDistance="150" selectionWeight="9" preparedActivity="threat" />
    <Event id="bank_robbery_mapped_interior_single_robber" title="Bank Robbery — One Armed Suspect Threatens the Banker" type="bank_robbery" audioIncidentType="bank_robbery" briefing="Dispatch reports one armed suspect threatening a banker inside a cataloged bank. Customers remain inside; enter through the mapped public doorway and handle the suspect's compliance, resistance, or escape response." criminalProfileIds="armed_robber" locationIds="downtown,pillbox,mission_row,los_santos.downtown.financial" branchIds="dispatch-bank-robbery-interior-attempt,dispatch-bank-robbery-escape,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="financial" requiredPhysicalType="Bank" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="1" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="40" responseResistWeight="35" responseFleeWeight="25" victimPool="bank_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="1" maximumSceneParticipants="2" severity="5" minimumCooldownSeconds="360" spawnDistance="145" selectionWeight="8" preparedActivity="threat" />
    <Event id="bank_robbery_mapped_interior_three_suspect_takeover" title="Bank Heist — Three Suspects Threaten Staff and Customers" type="bank_robbery" audioIncidentType="bank_robbery" briefing="Dispatch reports three armed suspects threatening bank staff and customers inside a cataloged branch. Enter by the mapped doorway, assess the group, and call for Backup if needed." criminalProfileIds="armed_robber" locationIds="downtown,pillbox,mission_row,los_santos.downtown.financial" branchIds="dispatch-bank-robbery-interior-attempt,dispatch-bank-robbery-gunfire,dispatch-bank-robbery-escape,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="financial" requiredPhysicalType="Bank" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="3" minimumSuspects="3" maximumSuspects="3" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="45" responseFleeWeight="30" victimPool="bank_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="2" maximumSceneParticipants="3" severity="5" minimumCooldownSeconds="420" spawnDistance="150" selectionWeight="9" preparedActivity="threat" />
    <Event id="bank_robbery_mapped_interior_gunfire_single" title="Bank Robbery — Shots Reported With One Suspect Inside" type="bank_robbery" audioIncidentType="shots_fired" briefing="Dispatch reports shots during a bank robbery. One armed suspect remains inside the cataloged branch with the banker and customers; use the mapped entrance and respond to the suspect's documented compliance or resistance branch." criminalProfileIds="armed_robber" locationIds="downtown,pillbox,mission_row,los_santos.downtown.financial" branchIds="dispatch-bank-robbery-gunfire,dispatch-bank-robbery-interior-attempt,dispatch-bank-robbery-escape,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="financial" requiredPhysicalType="Bank" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="1" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="20" responseResistWeight="55" responseFleeWeight="25" victimPool="bank_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="1" maximumSceneParticipants="2" severity="5" minimumCooldownSeconds="420" spawnDistance="150" selectionWeight="9" preparedActivity="threat" />
    <Event id="bank_robbery_mapped_interior_two_suspect_gunfire" title="Bank Robbery — Shots Reported During a Two-Suspect Heist" type="bank_robbery" audioIncidentType="shots_fired" briefing="Dispatch reports gunfire during a two-suspect bank robbery. The banker and customers are inside the cataloged branch; approach the mapped public doorway and request Backup if you need support." criminalProfileIds="armed_robber" locationIds="downtown,pillbox,mission_row,los_santos.downtown.financial" branchIds="dispatch-bank-robbery-gunfire,dispatch-bank-robbery-escape,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="financial" requiredPhysicalType="Bank" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" minimumSuspects="2" maximumSuspects="2" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="45" responseFleeWeight="30" victimPool="bank_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="1" maximumSceneParticipants="3" severity="5" minimumCooldownSeconds="420" spawnDistance="150" selectionWeight="9" preparedActivity="threat" />
    <Event id="bank_robbery_mapped_interior_escape_attempt" title="Bank Robbery — Suspects May Flee Through the Entrance" type="bank_robbery" audioIncidentType="bank_robbery" briefing="Dispatch reports an armed bank robbery in progress at a cataloged branch. The suspects are threatening the banker and customers and may attempt to flee on foot; enter through the mapped doorway and continue the documented pursuit if they escape." criminalProfileIds="armed_robber" locationIds="downtown,pillbox,mission_row,los_santos.downtown.financial" branchIds="dispatch-bank-robbery-escape,dispatch-bank-robbery-interior-attempt,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="financial" requiredPhysicalType="Bank" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" minimumSuspects="2" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="15" responseResistWeight="25" responseFleeWeight="60" victimPool="bank_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="1" maximumSceneParticipants="2" severity="5" minimumCooldownSeconds="390" spawnDistance="150" selectionWeight="9" preparedActivity="threat" />
    <Event id="bank_robbery_mapped_interior_customers_threatened" title="Bank Heist — Customers Held at Gunpoint With the Banker" type="bank_robbery" audioIncidentType="bank_robbery" briefing="An armed suspect is threatening the banker and several customers inside a cataloged bank. Approach the mapped entrance, assess the interior, and request Backup if the number of threatened people warrants it." criminalProfileIds="armed_robber" locationIds="downtown,pillbox,mission_row,los_santos.downtown.financial" branchIds="dispatch-bank-robbery-interior-attempt,dispatch-bank-robbery-gunfire,dispatch-bank-robbery-escape,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="financial" requiredPhysicalType="Bank" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="40" responseFleeWeight="25" victimPool="bank_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="2" maximumSceneParticipants="3" severity="5" minimumCooldownSeconds="420" spawnDistance="145" selectionWeight="8" preparedActivity="threat" />
    <Event id="bank_robbery_mapped_interior_group_resistance" title="Bank Heist — Group Resists Police Entry" type="bank_robbery" audioIncidentType="bank_robbery" briefing="An armed group is threatening bank staff and customers inside a cataloged branch. The suspects may resist when confronted; enter using the mapped doorway, keep the public safe, and call Backup as needed." criminalProfileIds="armed_robber" locationIds="downtown,pillbox,mission_row,los_santos.downtown.financial" branchIds="dispatch-bank-robbery-interior-attempt,dispatch-bank-robbery-gunfire,dispatch-bank-robbery-escape,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="financial" requiredPhysicalType="Bank" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" minimumSuspects="2" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="20" responseResistWeight="55" responseFleeWeight="25" victimPool="bank_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="2" maximumSceneParticipants="3" severity="5" minimumCooldownSeconds="420" spawnDistance="150" selectionWeight="9" preparedActivity="threat" />
    <Event id="clothing_store_mapped_interior_group_robbery" title="Clothing Store Jacking — Clerk and Customers Threatened" type="store_robbery" audioIncidentType="store_robbery" briefing="Dispatch reports an armed group robbery inside a mapped clothing store. The clerk and customers are threatened; suspects may comply, resist with gunfire, or flee on foot through the mapped doorway." criminalProfileIds="armed_robber,organized_criminal,gang_robber" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,los_santos.vespucci.commercial,los_santos.vinewood.nightlife,los_santos.mirror_park.edge" branchIds="dispatch-store-robbery-interior-custody,dispatch-store-robbery-group-gunfire,dispatch-store-robbery-fleeing-foot,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="ClothingStore" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" minimumSuspects="2" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="30" responseResistWeight="40" responseFleeWeight="30" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="1" maximumSceneParticipants="2" severity="5" minimumCooldownSeconds="360" spawnDistance="145" selectionWeight="8" preparedActivity="threat" />
    <Event id="clothing_store_mapped_interior_group_gunfire" title="Clothing Store Robbery — Group Gunfire Inside" type="store_robbery" audioIncidentType="shots_fired" briefing="Dispatch reports shots fired during an armed robbery in a mapped clothing store. The clerk and customers remain inside with a group of suspects; enter through the mapped doorway and request Backup if needed." criminalProfileIds="armed_robber,organized_criminal,gang_robber" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,los_santos.vespucci.commercial,los_santos.vinewood.nightlife,los_santos.mirror_park.edge" branchIds="dispatch-store-robbery-group-gunfire,dispatch-store-robbery-fleeing-foot,dispatch-store-robbery-interior-custody,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="ClothingStore" sceneBehavior="interior_robbery_custody" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" minimumSuspects="2" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="20" responseResistWeight="45" responseFleeWeight="35" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="1" maximumSceneParticipants="2" severity="5" minimumCooldownSeconds="390" spawnDistance="150" selectionWeight="9" preparedActivity="threat" />
    <Event id="clothing_store_mapped_interior_armed_attack" title="Armed Assault on a Clothing Store Clerk" type="violent_disturbance" audioIncidentType="shots_fired" briefing="Dispatch reports an armed attack on a clerk inside a mapped clothing store. Customers are also inside; enter through the mapped doorway and respond to compliance, resistance, or flight." criminalProfileIds="armed_robber,organized_criminal" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,los_santos.vespucci.commercial,los_santos.vinewood.nightlife,los_santos.mirror_park.edge" branchIds="dispatch-interior-attack-attempt,dispatch-attempted-murder,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="ClothingStore" sceneBehavior="interior_attack_attempt" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="1" maximumSceneParticipants="2" severity="5" minimumCooldownSeconds="330" spawnDistance="145" selectionWeight="8" preparedActivity="threat" />
    <Event id="clothing_store_mapped_interior_clerk_assault" title="Unarmed Assault on a Clothing Store Clerk" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports an unarmed assault in progress on a clerk inside a mapped clothing store. Customers are present; the suspect may comply, resist, or flee on foot as you arrive." criminalProfileIds="disturbance_subject" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,los_santos.vespucci.commercial,los_santos.vinewood.nightlife,los_santos.mirror_park.edge" branchIds="dispatch-interior-clerk-assault,dispatch-interior-attack-attempt,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="ClothingStore" sceneBehavior="interior_clerk_assault" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="false" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_attack" suspectResponseBranches="comply,resist,flee" responseComplyWeight="30" responseResistWeight="35" responseFleeWeight="35" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="1" maximumSceneParticipants="2" severity="5" minimumCooldownSeconds="300" spawnDistance="140" selectionWeight="8" preparedActivity="assault" />
    <Event id="clothing_store_mapped_interior_customer_assault" title="Customer Assault Inside a Clothing Store — Clerk Present" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports an unarmed assault on a customer inside a mapped clothing store. A clerk is present; respond through the mapped entrance and resolve the suspect's compliance, resistance, or flight." criminalProfileIds="disturbance_subject" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,los_santos.vespucci.commercial,los_santos.vinewood.nightlife,los_santos.mirror_park.edge" branchIds="dispatch-interior-clerk-assault,dispatch-interior-attack-attempt,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="ClothingStore" sceneBehavior="interior_clerk_assault" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="false" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="victim_attack" suspectResponseBranches="comply,resist,flee" responseComplyWeight="30" responseResistWeight="35" responseFleeWeight="35" victimPool="shop_customer" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_clerk" minimumSceneParticipants="1" maximumSceneParticipants="1" severity="5" minimumCooldownSeconds="300" spawnDistance="140" selectionWeight="8" preparedActivity="assault" />
```

Changed code beginning at line 189:

```xml
    <Event id="clothing_store_mapped_interior_clerk_murder" title="Murder Inside a Clothing Store — Clerk Dead, Suspect Nearby" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports a murder inside a mapped clothing store. The clerk is already dead and a suspect remains nearby; customers are present, and the Player must enter, respond, and investigate the victim." criminalProfileIds="armed_robber,organized_criminal,gang_robber" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,los_santos.vespucci.commercial,los_santos.vinewood.nightlife,los_santos.mirror_park.edge" branchIds="dispatch-interior-murder-victim,dispatch-single-suspect-escalation,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="ClothingStore" sceneBehavior="murder_victim_interior" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="murder_scene" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="shop_clerk" victimStartsDead="true" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="1" maximumSceneParticipants="2" severity="5" minimumCooldownSeconds="360" spawnDistance="145" selectionWeight="8" preparedActivity="murder" />
    <Event id="clothing_store_mapped_interior_customer_murder" title="Murder Inside a Clothing Store — Customer Dead, Clerk Present" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports a murder inside a mapped clothing store. A customer is already dead and the clerk remains at the scene; enter through the mapped doorway, respond to the suspect, and investigate the victim." criminalProfileIds="armed_robber,organized_criminal,gang_robber" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,los_santos.vespucci.commercial,los_santos.vinewood.nightlife,los_santos.mirror_park.edge" branchIds="dispatch-interior-murder-victim,dispatch-single-suspect-escalation,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="ClothingStore" sceneBehavior="murder_victim_interior" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="murder_scene" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="shop_customer" victimStartsDead="true" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_clerk" minimumSceneParticipants="1" maximumSceneParticipants="1" severity="5" minimumCooldownSeconds="360" spawnDistance="145" selectionWeight="8" preparedActivity="murder" />
```

Changed code beginning at line 192:

```xml
    <Event id="exterior_drug_exchange" title="Possible Drug Exchange Outdoors" type="drug_activity" audioIncidentType="drug_activity" briefing="Dispatch reports a possible drug exchange at an outdoor location. Observe the people meeting at the reported scene." criminalProfileIds="disturbance_subject,nervous_suspect" locationIds="los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-exterior-drug-exchange,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" sceneBehavior="paired_exchange" sceneAnimationDictionary="mp_common" sceneAnimationPrimaryClip="givetake1_a" sceneAnimationSecondaryClip="givetake1_b" minimumSuspects="2" maximumSuspects="5" suspectCount="2" severity="2" minimumCooldownSeconds="150" spawnDistance="90" requiresVehicle="false" vehicleStagingRequired="false" requiresMappedInterior="false" requiresExteriorScene="true" mobile="false" preparedActivity="exchange" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="exterior_criminal_meeting" title="Criminal Meeting at an Outdoor Location" type="criminal_gathering" audioIncidentType="criminal_gathering" briefing="Dispatch reports a group meeting at an outdoor location. Observe the group before taking action." criminalProfileIds="organized_criminal,disturbance_subject" locationIds="los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-exterior-criminal-meeting,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" sceneBehavior="paired_meeting" sceneAnimationDictionary="mp_ped_interaction" sceneAnimationPrimaryClip="handshake_guy_a" sceneAnimationSecondaryClip="handshake_guy_b" requiresVehicle="false" mobile="false" armed="false" suspectCount="2" minimumSuspects="2" maximumSuspects="5" severity="2" minimumCooldownSeconds="150" spawnDistance="90" vehicleStagingRequired="false" requiresMappedInterior="false" requiresExteriorScene="true" preparedActivity="gathering" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="exterior_weapons_exchange" title="Possible Weapons Exchange Outdoors" type="weapons_incident" audioIncidentType="weapons_incident" briefing="Dispatch reports a possible weapons exchange at an outdoor location. Approach cautiously." criminalProfileIds="organized_criminal,armed_offender" locationIds="los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-exterior-weapons-exchange,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" sceneBehavior="paired_exchange" sceneAnimationDictionary="mp_common" sceneAnimationPrimaryClip="givetake1_a" sceneAnimationSecondaryClip="givetake1_b" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" minimumSuspects="2" maximumSuspects="5" preArrivalActivity="guard" suspectResponseBranches="comply,resist,flee" severity="4" minimumCooldownSeconds="180" spawnDistance="95" vehicleStagingRequired="false" requiresMappedInterior="false" requiresExteriorScene="true" preparedActivity="exchange" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
    <Event id="exterior_murder_victim_scene" title="Outdoor Murder Scene - Suspect Near Victim" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports a murder victim at an outdoor location with a suspect nearby. Secure the scene and investigate the victim." criminalProfileIds="armed_offender,organized_criminal" locationIds="los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-exterior-murder-victim,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" sceneBehavior="murder_victim_exterior" victimPool="ambient_civilian" victimStartsDead="true" minimumVictims="1" maximumVictims="1" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="5" preArrivalActivity="murder_scene" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="25" responseFleeWeight="40" selectionWeight="1" severity="5" minimumCooldownSeconds="300" spawnDistance="105" vehicleStagingRequired="false" requiresMappedInterior="false" requiresExteriorScene="true" preparedActivity="murder" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" />
    <Event id="exterior_attempted_murder" title="Attempted Murder at an Outdoor Location" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Dispatch reports a living victim under armed threat at an outdoor location. The incident is still active." criminalProfileIds="armed_offender,organized_criminal,gang_robber" locationIds="mission_row,strawberry,la_mesa,cypress,los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-attempted-murder,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="3" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="110" vehicleStagingRequired="false" requiresMappedInterior="false" requiresExteriorScene="true" preparedActivity="threat" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" />
    <Event id="exterior_kidnapping_attempt" title="Outdoor Kidnapping Attempt - Victim Present" type="kidnapping" audioIncidentType="kidnapping" briefing="Dispatch reports an armed suspect threatening to abduct a person on foot. The victim remains at the reported outdoor location." criminalProfileIds="organized_criminal,armed_offender" locationIds="mission_row,strawberry,la_mesa,cypress,los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-kidnapping-attempt,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="2" preArrivalActivity="victim_threat" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimResponseBranches="attempt_escape,remain_threatened" victimAttemptEscapeWeight="60" victimRemainThreatenedWeight="40" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="110" vehicleStagingRequired="false" requiresMappedInterior="false" requiresExteriorScene="true" preparedActivity="threat" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" />
    <Event id="exterior_group_shootout" title="Outdoor Group Shootout - Civilian at Risk" type="shots_fired" audioIncidentType="shots_fired" briefing="Dispatch reports an armed group firing at an outdoor location with a civilian at risk. Approach cautiously." criminalProfileIds="armed_offender,organized_criminal,gang_robber" locationIds="downtown,strawberry,vespucci,del_perro,sandy_shores,paleto_bay,blaine.chumash.coast,los_santos.banham.canyon" branchIds="dispatch-team-backup-group,dispatch-foot-suspect-solo,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" requiresVehicle="false" mobile="false" armed="true" suspectCount="2" minimumSuspects="2" maximumSuspects="3" preArrivalActivity="group_shootout" suspectResponseBranches="comply,resist,flee" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="240" spawnDistance="110" vehicleStagingRequired="false" requiresMappedInterior="false" requiresExteriorScene="true" preparedActivity="shooting" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" />
      <!-- Authored Dispatch expansion; physical activity begins after acceptance. -->
    <Event id="disturbance_stalking" title="Stalking and unwanted following" type="disturbance" audioIncidentType="stalking" briefing="Stalking and unwanted following. Respond to the reported scene and assess the participants." criminalProfileIds="disturbance_subject,nervous_suspect" locationIds="mission_row,strawberry,la_mesa,cypress,los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-attempted-murder,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" requiresVehicle="false" mobile="false" armed="false" suspectCount="1" minimumSuspects="1" maximumSuspects="1" preArrivalActivity="none" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="110" vehicleStagingRequired="false" requiresMappedInterior="false" requiresExteriorScene="true" preparedActivity="stalking" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" sceneBehavior="standard" victimStartsDead="false" selectionWeight="3" />
    <Event id="disturbance_lying_naked" title="Lying naked disturbance" type="disturbance" audioIncidentType="lying_naked" briefing="Lying naked disturbance. Respond to the reported scene and assess the participants." criminalProfileIds="dispatch_naked_subject" locationIds="mission_row,strawberry,la_mesa,cypress,los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-attempted-murder,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" requiresVehicle="false" mobile="false" armed="false" suspectCount="1" minimumSuspects="1" maximumSuspects="1" preArrivalActivity="none" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="110" vehicleStagingRequired="false" requiresMappedInterior="false" requiresExteriorScene="true" preparedActivity="lying" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" sceneBehavior="standard" victimStartsDead="false" selectionWeight="3" />
    <Event id="exterior_physical_assault" title="Physical assault on a pedestrian" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Physical assault on a pedestrian. Respond to the reported scene and assess the participants." criminalProfileIds="disturbance_subject,nervous_suspect" locationIds="mission_row,strawberry,la_mesa,cypress,los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-attempted-murder,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" requiresVehicle="false" mobile="false" armed="false" suspectCount="1" minimumSuspects="1" maximumSuspects="1" preArrivalActivity="none" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="110" vehicleStagingRequired="false" requiresMappedInterior="false" requiresExteriorScene="true" preparedActivity="assault" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" sceneBehavior="standard" victimStartsDead="false" selectionWeight="3" />
    <Event id="exterior_knife_assault" title="Knife assault on a pedestrian" type="violent_disturbance" audioIncidentType="armed_suspect" briefing="Knife assault on a pedestrian. Respond to the reported scene and assess the participants." criminalProfileIds="dispatch_knife_assailant" locationIds="mission_row,strawberry,la_mesa,cypress,los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-attempted-murder,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="1" preArrivalActivity="none" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="110" vehicleStagingRequired="false" requiresMappedInterior="false" requiresExteriorScene="true" preparedActivity="assault" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" sceneBehavior="standard" victimStartsDead="false" selectionWeight="3" />
    <Event id="exterior_gun_assault" title="Gun assault on a pedestrian" type="shots_fired" audioIncidentType="shots_fired" briefing="Gun assault on a pedestrian. Respond to the reported scene and assess the participants." criminalProfileIds="armed_offender,organized_criminal" locationIds="mission_row,strawberry,la_mesa,cypress,los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-attempted-murder,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" requiresVehicle="false" mobile="false" armed="true" suspectCount="1" minimumSuspects="1" maximumSuspects="1" preArrivalActivity="active_shooter" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="110" vehicleStagingRequired="false" requiresMappedInterior="false" requiresExteriorScene="true" preparedActivity="shooting" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" sceneBehavior="standard" victimStartsDead="false" selectionWeight="3" />
    <Event id="exterior_people_theft" title="Theft from a pedestrian" type="disturbance" audioIncidentType="people_theft" briefing="Theft from a pedestrian. Respond to the reported scene and assess the participants." criminalProfileIds="disturbance_subject,nervous_suspect" locationIds="mission_row,strawberry,la_mesa,cypress,los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-attempted-murder,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" requiresVehicle="false" mobile="false" armed="false" suspectCount="1" minimumSuspects="1" maximumSuspects="1" preArrivalActivity="none" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="110" vehicleStagingRequired="false" requiresMappedInterior="false" requiresExteriorScene="true" preparedActivity="theft" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" sceneBehavior="standard" victimStartsDead="false" selectionWeight="3" />
    <Event id="exterior_driver_assault" title="Assault on a driver beside their vehicle" type="violent_disturbance" audioIncidentType="driver_assault" briefing="Assault on a driver beside their vehicle. Respond to the reported scene and assess the participants." criminalProfileIds="disturbance_subject,nervous_suspect" locationIds="mission_row,strawberry,la_mesa,cypress,los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-attempted-murder,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" requiresVehicle="false" mobile="false" armed="false" suspectCount="1" minimumSuspects="1" maximumSuspects="1" preArrivalActivity="none" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="110" vehicleStagingRequired="false" requiresMappedInterior="false" requiresExteriorScene="true" preparedActivity="driver_assault" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" sceneBehavior="standard" victimStartsDead="false" selectionWeight="3" />
    <Event id="road_vehicle_ramming" title="Vehicle ramming and roadside chaos" type="reckless_driver" audioIncidentType="ramming" briefing="Vehicle ramming and roadside chaos. Follow the developing road incident." criminalProfileIds="reckless_commuter,street_driver" locationIds="blaine.chumash.coast,blaine.zancudo.supply_route,los_santos.banham.canyon,blaine.grand_senora.airstrip" branchIds="dispatch-road-vehicle-speed-chaos,dispatch-reckless-driver,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="road_vehicle" requiresVehicle="true" mobile="true" startPursuitOnAccept="false" armed="false" suspectCount="1" minimumSuspects="1" maximumSuspects="3" severity="3" minimumCooldownSeconds="150" spawnDistance="135" selectionWeight="6" preparedActivity="ramming" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" vehicleActivity="moving_road" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" vehicleAssetTypes="escape_car,street_vehicle,crew_suv,cargo_van" />
    <Event id="road_vehicle_slow_disturbance" title="Slow-driving road disturbance" type="reckless_driver" audioIncidentType="slow_driving" briefing="Slow-driving road disturbance. Follow the developing road incident." criminalProfileIds="reckless_commuter,street_driver" locationIds="blaine.chumash.coast,blaine.zancudo.supply_route,los_santos.banham.canyon,blaine.grand_senora.airstrip" branchIds="dispatch-road-vehicle-speed-chaos,dispatch-reckless-driver,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="road_vehicle" requiresVehicle="true" mobile="true" startPursuitOnAccept="false" armed="false" suspectCount="1" minimumSuspects="1" maximumSuspects="3" severity="3" minimumCooldownSeconds="150" spawnDistance="135" selectionWeight="6" preparedActivity="driving_slow" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="30" responseFleeWeight="35" vehicleActivity="moving_road" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="1" maximumSceneParticipants="2" vehicleAssetTypes="escape_car,street_vehicle,crew_suv,cargo_van" />
    <Event id="interior_theft" title="Theft inside a store" type="disturbance" audioIncidentType="disturbance" briefing="Theft inside a store. Respond to the reported scene and assess the participants." criminalProfileIds="disturbance_subject,nervous_suspect" locationIds="mission_row,strawberry,la_mesa,cypress,los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-attempted-murder,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiresVehicle="false" mobile="false" armed="false" suspectCount="1" minimumSuspects="1" maximumSuspects="1" preArrivalActivity="none" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="110" vehicleStagingRequired="false" requiresMappedInterior="true" requiresExteriorScene="false" preparedActivity="theft" sceneParticipantPool="shop_customer" minimumSceneParticipants="2" maximumSceneParticipants="3" sceneBehavior="interior_activity" victimStartsDead="false" selectionWeight="3" requiredPhysicalType="Store" />
    <Event id="interior_group_dealing" title="Dealing inside a store" type="drug_activity" audioIncidentType="interior_dealing" briefing="Dealing inside a store. Respond to the reported scene and assess the participants." criminalProfileIds="disturbance_subject,nervous_suspect" locationIds="mission_row,strawberry,la_mesa,cypress,los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-attempted-murder,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiresVehicle="false" mobile="false" armed="false" suspectCount="4" minimumSuspects="4" maximumSuspects="5" preArrivalActivity="none" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="110" vehicleStagingRequired="false" requiresMappedInterior="true" requiresExteriorScene="false" preparedActivity="exchange" sceneParticipantPool="shop_customer" minimumSceneParticipants="2" maximumSceneParticipants="3" sceneBehavior="interior_activity" victimStartsDead="false" selectionWeight="3" requiredPhysicalType="Store" />
    <Event id="interior_group_gathering" title="Group gathering inside a store" type="criminal_gathering" audioIncidentType="interior_gathering" briefing="Group gathering inside a store. Respond to the reported scene and assess the participants." criminalProfileIds="disturbance_subject,nervous_suspect" locationIds="mission_row,strawberry,la_mesa,cypress,los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-attempted-murder,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiresVehicle="false" mobile="false" armed="false" suspectCount="4" minimumSuspects="4" maximumSuspects="5" preArrivalActivity="none" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="110" vehicleStagingRequired="false" requiresMappedInterior="true" requiresExteriorScene="false" preparedActivity="gathering" sceneParticipantPool="shop_customer" minimumSceneParticipants="2" maximumSceneParticipants="3" sceneBehavior="interior_activity" victimStartsDead="false" selectionWeight="3" requiredPhysicalType="Store" />
    <Event id="interior_massacre_chaos" title="Massacre aftermath inside a store" type="violent_disturbance" audioIncidentType="massacre" briefing="Massacre aftermath inside a store. An armed group remains with the victim and surviving witnesses." criminalProfileIds="armed_robber,organized_criminal,gang_robber" locationIds="mission_row,pillbox,downtown,vespucci,del_perro,la_mesa,vinewood,mirror_park,los_santos.vespucci.commercial,los_santos.vinewood.nightlife,los_santos.mirror_park.edge" branchIds="dispatch-interior-murder-victim,dispatch-single-suspect-escalation,dispatch-team-backup-group,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiredPhysicalType="ClothingStore" sceneBehavior="murder_victim_interior" requiresMappedInterior="true" vehicleStagingRequired="false" requiresVehicle="false" mobile="false" armed="true" suspectCount="4" minimumSuspects="4" maximumSuspects="5" preArrivalActivity="murder_scene" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="shop_clerk" victimStartsDead="true" minimumVictims="1" maximumVictims="1" sceneParticipantPool="shop_customer" minimumSceneParticipants="3" maximumSceneParticipants="4" severity="5" minimumCooldownSeconds="360" spawnDistance="145" selectionWeight="8" preparedActivity="murder" additionalDeadVictims="2" />
    <Event id="exterior_massacre_chaos" title="Massacre aftermath outdoors" type="violent_disturbance" audioIncidentType="massacre" briefing="Massacre aftermath outdoors. An armed group remains with the victim and surviving witnesses." criminalProfileIds="armed_offender,organized_criminal" locationIds="los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-exterior-murder-victim,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" sceneBehavior="murder_victim_exterior" victimPool="ambient_civilian" victimStartsDead="true" minimumVictims="1" maximumVictims="1" requiresVehicle="false" mobile="false" armed="true" suspectCount="4" minimumSuspects="4" maximumSuspects="5" preArrivalActivity="murder_scene" suspectResponseBranches="comply,resist,flee" responseComplyWeight="35" responseResistWeight="25" responseFleeWeight="40" selectionWeight="1" severity="5" minimumCooldownSeconds="300" spawnDistance="105" vehicleStagingRequired="false" requiresMappedInterior="false" requiresExteriorScene="true" preparedActivity="murder" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="3" maximumSceneParticipants="4" additionalDeadVictims="2" />
    <Event id="exterior_populated_chaos" title="Group assault in a populated place" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Group assault in a populated place. Respond to the reported scene and assess the participants." criminalProfileIds="disturbance_subject,nervous_suspect" locationIds="mission_row,strawberry,la_mesa,cypress,los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-attempted-murder,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" requiresVehicle="false" mobile="false" armed="false" suspectCount="4" minimumSuspects="4" maximumSuspects="5" preArrivalActivity="none" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="110" vehicleStagingRequired="false" requiresMappedInterior="false" requiresExteriorScene="true" preparedActivity="assault" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="4" maximumSceneParticipants="5" sceneBehavior="standard" victimStartsDead="false" selectionWeight="3" />
    <Event id="exterior_store_assault" title="Group assault outside a store" type="violent_disturbance" audioIncidentType="violent_disturbance" briefing="Group assault outside a store. Respond to the reported scene and assess the participants." criminalProfileIds="disturbance_subject,nervous_suspect" locationIds="mission_row,strawberry,la_mesa,cypress,los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-attempted-murder,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="commercial" requiresVehicle="false" mobile="false" armed="false" suspectCount="4" minimumSuspects="4" maximumSuspects="5" preArrivalActivity="none" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="shop_clerk" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="110" vehicleStagingRequired="false" requiresMappedInterior="false" requiresExteriorScene="true" preparedActivity="assault" sceneParticipantPool="shop_customer" minimumSceneParticipants="2" maximumSceneParticipants="3" sceneBehavior="standard" victimStartsDead="false" selectionWeight="3" />
    <Event id="exterior_threat_gathering" title="Threatening group gathering" type="armed_suspect" audioIncidentType="armed_suspect" briefing="Threatening group gathering. Respond to the reported scene and assess the participants." criminalProfileIds="armed_offender,organized_criminal" locationIds="mission_row,strawberry,la_mesa,cypress,los_santos.south.industrial,los_santos.strawberry.storage,los_santos.la_mesa.garages" branchIds="dispatch-attempted-murder,dispatch-team-backup-group,dispatch-single-suspect-escalation,Fleeing_Event,Resisting_Event,Compliant_Event" locationContext="any" requiresVehicle="false" mobile="false" armed="true" suspectCount="4" minimumSuspects="4" maximumSuspects="5" preArrivalActivity="none" suspectResponseBranches="comply,resist,flee" responseComplyWeight="25" responseResistWeight="35" responseFleeWeight="40" victimPool="ambient_civilian" minimumVictims="1" maximumVictims="1" severity="5" minimumCooldownSeconds="300" spawnDistance="110" vehicleStagingRequired="false" requiresMappedInterior="false" requiresExteriorScene="true" preparedActivity="threat" sceneParticipantPool="ambient_civilian" minimumSceneParticipants="2" maximumSceneParticipants="3" sceneBehavior="standard" victimStartsDead="false" selectionWeight="3" />
```

## LSWorldAmbientBehavior.cs

Changed code beginning at line 12105:

```csharp
        // Dispatch retains the occurrence and custody roster; AmbientWorld owns
        // physical scene tasks. Sampling is bounded to this roster at 750 ms.
        internal bool PrepareDispatchActivityResources(LSPDDispatchEvent incident)
        {
            try { return PrepareDispatchActivityResourcesCore(incident); }
            catch (Exception ex)
            {
                Log("AMBIENT_WORLD_DISPATCH_RESOURCE_PENDING", "Occurrence=" + (incident == null ? "" : incident.Id)
                    + "; NativeResourceFailure=" + ex.GetType().Name);
                return false;
            }
        }

        private bool PrepareDispatchActivityResourcesCore(LSPDDispatchEvent incident)
        {
            if (incident == null || string.IsNullOrEmpty(incident.PreparedActivity)) return true;
            string action = incident.PreparedActivity;
            if ((action == "ramming" || action == "driver_assault") && !PrepareDispatchRammingTarget(incident)) return false;
            if ((action == "exchange" || action == "theft") && !PrepareDispatchExchangeProp(incident)) return false;
            if (action == "exchange")
                return LSImmersiveDictionaryAnimation.RequestDispatchAction("exchange_giver")
                    & LSImmersiveDictionaryAnimation.RequestDispatchAction("exchange_receiver");
            if (action == "theft") return LSImmersiveDictionaryAnimation.RequestDispatchAction("exchange_giver");
            if (new[] { "camera", "intoxicated", "lying" }.Contains(action))
                return LSImmersiveDictionaryAnimation.RequestDispatchAction(action);
            return LSImmersiveDictionaryAnimation.RequestDispatchAction("conversation")
                & LSImmersiveDictionaryAnimation.RequestDispatchAction("cower");
        }

        internal bool MaintainPreparedDispatchActivity(LSPDDispatchEvent incident, DateTime now, out string reason)
        {
            try { return MaintainPreparedDispatchActivityCore(incident, now, out reason); }
            catch (Exception ex)
            {
                reason = "FAILED:Activity native failure: " + ex.GetType().Name;
                Log("AMBIENT_WORLD_DISPATCH_ACTIVITY_FAILED", "Occurrence=" + (incident == null ? "" : incident.Id) + "; " + reason);
                return false;
            }
        }

        private bool MaintainPreparedDispatchActivityCore(LSPDDispatchEvent incident, DateTime now, out string reason)
        {
            reason = string.Empty;
            if (incident == null || string.IsNullOrEmpty(incident.PreparedActivity)) return true;
            if (!incident.OwnedByDispatch || incident.HasConvoyCustodyHandoff) return true;
            if (now < incident.PreparedActivityNextSampleAt) return incident.PreparedActivityConfirmed;
            incident.PreparedActivityNextSampleAt = now.AddMilliseconds(750);
            List<Ped> suspects = incident.Suspects.Where(p => p != null && p.Exists())
                .Concat(new[] { incident.Suspect }).Where(p => p != null && p.Exists())
                .GroupBy(p => p.Handle).Select(g => g.First()).ToList();
            if (!incident.PreparedActivityStarted)
            {
                if (suspects.Count != incident.SuspectCount || suspects.Any(p => p.IsDead))
                { reason = "Required suspect roster unavailable"; return false; }
                if (!PrepareDispatchActivityResources(incident))
                { reason = "Animation dictionary/clip is unavailable or still streaming"; return false; }
                incident.PreparedActivityStarted = true;
                incident.PreparedActivityStartedAt = now;
                incident.PreparedActivitySamples = 0;
            }
            bool encountered = incident.PlayerEncounterResponseStarted || incident.SuspectResponses.Count > 0
                || incident.SurrenderRequested || incident.PlayerHandcuffInProgress;
            bool allActive = suspects.Count == incident.SuspectCount - incident.ResolvedSuspectHandles.Count;
            var civilians = incident.AdditionalParticipants.Where(p => p != null && p.Exists()).ToList();
            if (incident.Victim != null && incident.Victim.Exists()) civilians.Insert(0, incident.Victim);
            allActive &= civilians.Count == incident.SceneParticipantCount + incident.VictimCount;
            if (incident.PreparedActivity == "exchange" && !encountered && suspects.Count >= 2)
                AttachDispatchExchangeProp(incident,
                    ((int)(now - incident.PreparedActivityStartedAt).TotalSeconds % 8) < 4 ? suspects[0] : suspects[1]);
            // Civilian roles remain independent of criminal response decisions.
            for (int i = 0; i < civilians.Count; i++)
            {
                Ped civilian = civilians[i];
                if (civilian.IsDead) { allActive &= incident.PreparedActivityConfirmed || (incident.VictimStartsDead && (civilian == incident.Victim
                    || incident.AdditionalParticipants.IndexOf(civilian) < incident.AdditionalDeadVictims)); continue; }
                string role = CivilianActivityRole(incident, i);
                allActive &= MaintainDispatchRole(incident, civilian, role, suspects.FirstOrDefault(), now);
            }
            for (int i = 0; i < suspects.Count; i++)
            {
                Ped actor = suspects[i];
                if (encountered || incident.ConvoyCustodySuspectHandles.Contains(actor.Handle) || incident.ArrestedSuspects.Any(p => p != null && p.Handle == actor.Handle)
                    || incident.CompliantSuspects.Any(p => p != null && p.Handle == actor.Handle)
                    || actor.IsDead || actor.Health < actor.MaxHealth - 15
                    || Function.Call<bool>(Hash.IS_PED_RAGDOLL, actor))
                    continue; // Injury, arrest, Backup and encounter tasks keep priority.
                string role = SuspectActivityRole(incident, i);
                if (role == "ramming" && incident.RammingImpactConfirmed && !actor.IsInVehicle()) role = "lookout";
                if (incident.Victim != null && incident.Victim.Exists() && incident.Victim.IsDead
                    && (role == "assault" || role == "shooting" || role == "driver_assault")) role = "lookout";
                allActive &= MaintainDispatchRole(incident, actor, role,
                    role == "exchange_giver" ? suspects.ElementAtOrDefault(1)
                    : role == "exchange_receiver" ? suspects[0] : incident.Victim, now);
            }
            if (encountered) return incident.PreparedActivityConfirmed;
            incident.PreparedActivitySamples++;
            if (allActive && incident.PreparedActivitySamples >= 2)
            {
                if (!incident.PreparedActivityConfirmed)
                    Log("AMBIENT_WORLD_DISPATCH_ACTIVITY_CONFIRMED", "Occurrence=" + incident.Id
                        + "; Activity=" + incident.PreparedActivity + "; Evidence="
                        + string.Join(",", incident.ActivityEvidenceHandles) + "; Owner=AmbientWorld");
                incident.PreparedActivityConfirmed = true;
                return true;
            }
            reason = "Awaiting physical activity: " + incident.PreparedActivity + "; confirmed actors="
                + incident.ActivityEvidenceHandles.Count;
            // Only bounded, targeted recovery. Never repeatedly reset progressing tasks.
            if (!encountered && now > incident.PreparedActivityStartedAt.AddSeconds(12))
            {
                if (incident.PreparedActivityRecoveryCount < 2)
                {
                    incident.PreparedActivityRecoveryCount++;
                    incident.PreparedActivityStartedAt = now;
                    incident.PreparedActivityConfirmed = false;
                    foreach (int handle in incident.ActivityTaskIssuedAt.Keys.ToArray())
                    {
                        DateTime last;
                        if (!incident.ActivityLastEvidenceAt.TryGetValue(handle, out last) || now > last.AddSeconds(5))
                        {
                            incident.ActivityTaskIssuedAt.Remove(handle);
                            incident.ActivityEvidenceHandles.Remove(handle);
                        }
                    }
                    Log("AMBIENT_WORLD_DISPATCH_ACTIVITY_RETRY", "Occurrence=" + incident.Id
                        + "; Attempt=" + incident.PreparedActivityRecoveryCount + "; " + reason);
                }
                else
                {
                    incident.PreparedActivityConfirmed = false;
                    reason = "FAILED:" + reason;
                }
            }
            return incident.PreparedActivityConfirmed;
        }

        private static string SuspectActivityRole(LSPDDispatchEvent incident, int index)
        {
            string action = incident.PreparedActivity;
            if (action.StartsWith("driving_") || action == "ramming") return action;
            if (action == "exchange") return index == 0 ? "exchange_giver" : index == 1
                ? "exchange_receiver" : index % 2 == 0 ? "lookout" : "conversation";
            if (action == "gathering") return index % 3 == 0 ? "conversation" : "lookout";
            if (action == "murder") return index % 2 == 0 ? "lookout" : "conversation";
            if (index > 1) return index % 2 == 0 ? "lookout" : "conversation";
            if (index == 1 && action != "assault" && action != "shooting") return "lookout";
            return action;
        }

        private static string CivilianActivityRole(LSPDDispatchEvent incident, int index)
        {
            string action = incident.PreparedActivity;
            if (index == 0 && (action == "stalking" || action == "theft")) return "walk";
            if (action.StartsWith("driving_") || action == "ramming") return "witness";
            if (action == "gathering" || action == "exchange" || action == "intoxicated" || action == "lying")
                return index % 2 == 0 ? "witness" : "walk";
            return index % 3 == 0 ? "cower" : index % 3 == 1 ? "witness" : "retreat";
        }

        private bool MaintainDispatchRole(LSPDDispatchEvent incident, Ped actor, string role, Ped target, DateTime now)
        {
            if (actor == null || !actor.Exists() || actor.IsDead) return false;
            actor.IsPositionFrozen = false;
            string previousRole;
            DateTime issuedAt;
            bool issued = incident.ActivityTaskIssuedAt.TryGetValue(actor.Handle, out issuedAt)
                && incident.ActivityRoles.TryGetValue(actor.Handle, out previousRole) && previousRole == role;
            Vector3 previous;
            bool moved = incident.ActivitySamplePositions.TryGetValue(actor.Handle, out previous)
                && actor.Position.DistanceTo(previous) > 0.25f;
            incident.ActivitySamplePositions[actor.Handle] = actor.Position;
            bool active = false;
            if (role.StartsWith("driving_") || role == "ramming")
                active = MaintainDispatchDriving(incident, actor, role, issued, moved);
            else if ((role == "threat" || role == "carjacking") && incident.Armed
                && target != null && target.Exists() && !target.IsDead)
            {
                // Script-task hashes are joaat(SCRIPT_TASK_*), not native hashes.
                int status = Function.Call<int>(Hash.GET_SCRIPT_TASK_STATUS, actor, 0x6134071B);
                active = issued && (status == 0 || status == 1)
                    && Function.Call<bool>(Hash.IS_PED_ARMED, actor, 4);
                if (!issued || (!active && now > issuedAt.AddSeconds(6)))
                {
                    Function.Call(Hash.TASK_AIM_GUN_AT_ENTITY, actor, target, -1, true);
                    issuedAt = now;
                }
            }
            else if (role == "driver_assault" && target != null && target.Exists()
                && incident.VictimVehicle != null && incident.VictimVehicle.Exists()
                && target.Position.DistanceTo(incident.VictimVehicle.Position) > 5f)
            {
                if (!issued)
                {
                    Function.Call(Hash.TASK_GO_TO_ENTITY, target, incident.VictimVehicle, -1, 3f, 1f, 0f, 0);
                    Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY, actor, target, 0f, -1f, 0f, 1.4f, -1, 1f, true);
                }
                active = moved;
            }
            else if (role == "assault" || role == "driver_assault" || role == "shooting")
            {
                if (target == null || !target.Exists() || target.IsDead) return false;
                active = actor.IsInCombatAgainst(target) && (role != "shooting" || actor.IsShooting
                    || incident.ActivityEvidenceHandles.Contains(actor.Handle));
                if (!issued || (role == "driver_assault" && !actor.IsInCombatAgainst(target))) actor.Task.Combat(target);
            }
            else if (role == "stalking" || role == "theft")
            {
                if (target == null || !target.Exists() || target.IsDead) return false;
                int stage;
                incident.ActivityStages.TryGetValue(actor.Handle, out stage);
                if (role == "theft" && stage == 2)
                {
                    active = moved;
                    if (!issued)
                    {
                        Vector3 center = incident.Origin;
                        Function.Call(Hash.TASK_WANDER_IN_AREA, actor, center.X, center.Y, center.Z, 7f, 2, 3);
                    }
                }
                else if (role == "theft" && stage == 1)
                {
                    active = LSImmersiveDictionaryAnimation.IsDispatchActionPlaying(actor, "exchange_giver");
                    if (now > issuedAt.AddSeconds(2))
                    {
                        incident.ActivityStages[actor.Handle] = 2;
                        incident.ActivityTaskIssuedAt.Remove(actor.Handle);
                        return active;
                    }
                }
                else
                {
                    active = moved && actor.Position.DistanceTo(target.Position) < 30f;
                    if (!issued) Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY, actor, target,
                        0f, role == "theft" ? -0.6f : -3f, 0f, role == "theft" ? 1.8f : 1.1f, -1, 0.6f, true);
                    if (role == "theft" && actor.Position.DistanceTo(target.Position) < 1.6f)
                    {
                        actor.Task.ClearAll();
                        if (!LSImmersiveDictionaryAnimation.TryPlayDispatchAction(actor, "exchange_giver", false)) return false;
                        AttachDispatchExchangeProp(incident, actor);
                        incident.ActivityStages[actor.Handle] = 1;
                        issuedAt = now;
                    }
                }
            }
            else if (role == "walk" || role == "lookout" || role == "retreat")
            {
                active = moved;
                if (!issued || (!moved && now > issuedAt.AddSeconds(7)))
                {
                    Vector3 point = incident.Origin;
                    if (role == "retreat" && target != null && target.Exists()
                        && !incident.SceneResolution.IsInteriorScene) actor.Task.ReactAndFlee(target);
                    else Function.Call(Hash.TASK_WANDER_IN_AREA, actor, point.X, point.Y, point.Z,
                        incident.SceneResolution.IsInteriorScene ? 2.5f : 7f, 2, 3);
                    issuedAt = now;
                }
            }
            else
            {
                string action = role == "witness" ? "conversation" : role == "carjacking" ? "threat" : role;
                active = LSImmersiveDictionaryAnimation.IsDispatchActionPlaying(actor, action);
                // Exchange clips alternate naturally with watch/conversation time.
                bool shortClip = action.StartsWith("exchange_");
                if (!issued || (!active && now > issuedAt.AddSeconds(shortClip ? 4 : 6)))
                {
                    if (target != null && target.Exists()) actor.Task.LookAt(target, 3000);
                    if (action == "camera" && !EnsureDispatchCamera(incident, actor)) return false;
                    if (!LSImmersiveDictionaryAnimation.TryPlayDispatchAction(actor, action, !shortClip)) return false;
                    issuedAt = now;
                }
            }
            if (!issued) issuedAt = now;
            incident.ActivityTaskIssuedAt[actor.Handle] = issuedAt;
            incident.ActivityRoles[actor.Handle] = role;
            if (active)
            {
                incident.ActivityEvidenceHandles.Add(actor.Handle);
                incident.ActivityLastEvidenceAt[actor.Handle] = now;
            }
            DateTime lastEvidence;
            return active || (incident.ActivityLastEvidenceAt.TryGetValue(actor.Handle, out lastEvidence)
                && now < lastEvidence.AddSeconds(5));
        }

        private bool MaintainDispatchDriving(LSPDDispatchEvent incident, Ped actor, string role, bool issued, bool moved)
        {
            Vehicle vehicle = incident.VehicleForSuspect(actor);
            if (vehicle == null || !vehicle.Exists() || !actor.IsInVehicle(vehicle)) return false;
            if (role == "ramming" && incident.VictimVehicle != null && incident.VictimVehicle.Exists()
                && !incident.RammingImpactConfirmed
                && vehicle.Position.DistanceTo(incident.VictimVehicle.Position) < 10f
                && Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, incident.VictimVehicle, vehicle, true))
            {
                incident.RammingImpactConfirmed = true;
                Log("AMBIENT_WORLD_DISPATCH_RAMMING_IMPACT_CONFIRMED", "Occurrence=" + incident.Id
                    + "; Vehicle=" + vehicle.Handle + "; Target=" + incident.VictimVehicle.Handle
                    + "; Native=HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY; TaskAssignmentAlone=false");
            }
            if (role == "ramming" && incident.RammingImpactConfirmed)
            {
                if (!Function.Call<bool>(Hash.IS_VEHICLE_DRIVEABLE, vehicle, false)) actor.Task.LeaveVehicle();
                return true;
            }
            if (!Function.Call<bool>(Hash.IS_VEHICLE_DRIVEABLE, vehicle, false)) return false;
            int driver = Function.Call<int>(Hash.GET_PED_IN_VEHICLE_SEAT, vehicle, -1, false);
            if (driver != actor.Handle)
                return driver != 0 && moved && vehicle.Speed > 1f; // Actual occupied passenger role.
            if (!issued)
            {
                Vector3 road = World.GetNextPositionOnStreet(vehicle.Position);
                if (road == Vector3.Zero || road.DistanceTo(vehicle.Position) > 40f) return false;
                vehicle.IsPositionFrozen = false;
                Function.Call(Hash.SET_VEHICLE_ENGINE_ON, vehicle, true, true, false);
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, vehicle, false);
                Function.Call(Hash.SET_VEHICLE_BRAKE, vehicle, false);
                float speed = role == "driving_slow" ? 3.5f : 28f;
                if (role == "ramming" && incident.VictimVehicle != null && incident.VictimVehicle.Exists())
                    Function.Call(Hash.TASK_VEHICLE_MISSION, actor, vehicle, incident.VictimVehicle,
                        2, 20f, 786468, 2f, 1f, true); // MISSION_RAM targets an actual catalog vehicle.
                else Function.Call(Hash.TASK_VEHICLE_DRIVE_WANDER, actor, vehicle, speed,
                    role == "driving_slow" ? 786603 : 786468);
            }
            // A task assignment or a running engine is never movement evidence.
            return role != "ramming" && moved && vehicle.Speed > (role == "driving_slow" ? 0.8f : 5f);
        }

        private bool PrepareDispatchRammingTarget(LSPDDispatchEvent incident)
        {
            if (incident.VictimVehicle != null && incident.VictimVehicle.Exists()) return true;
            Vector3 position;
            float heading;
            string reason;
            if (!TryResolveAdditionalDispatchVehicleStaging(incident.SceneResolution, out position, out heading, out reason))
                return false;
            string name = (incident.VehicleModelCandidates ?? new List<string>()).LastOrDefault();
            if (string.IsNullOrEmpty(name)) name = incident.VehicleModel;
            var model = new Model(name);
            if (!model.IsValid || !model.IsInCdImage) return false;
            model.Request();
            if (!model.IsLoaded) return false;
            Vehicle target = World.CreateVehicle(model, position, heading);
            model.MarkAsNoLongerNeeded();
            if (target == null || !target.Exists()) return false;
            target.IsPersistent = true;
            target.IsPositionFrozen = false;
            incident.VictimVehicle = target;
            return true;
        }

        private bool PrepareDispatchExchangeProp(LSPDDispatchEvent incident)
        {
            if (incident.ActivityProps.Any(p => p != null && p.Exists())) return true;
            Model model;
            if (incident.IncidentType == "weapons_incident")
            {
                int weapon = unchecked((int)StringHash.AtStringHash(incident.WeaponName, 0));
                model = new Model(Function.Call<int>(Hash.GET_WEAPONTYPE_MODEL, weapon));
            }
            else model = new Model(incident.PreparedActivity == "theft" ? "prop_ld_wallet_01" : "prop_cash_pile_01");
            if (!model.IsValid || !model.IsInCdImage) return false;
            model.Request();
            if (!model.IsLoaded) return false;
            Prop prop = World.CreateProp(model, incident.Suspect.Position, false, false);
            model.MarkAsNoLongerNeeded();
            if (prop == null || !prop.Exists()) return false;
            Function.Call(Hash.SET_ENTITY_VISIBLE, prop, false, false);
            incident.ActivityProps.Add(prop);
            return true;
        }

        private void AttachDispatchExchangeProp(LSPDDispatchEvent incident, Ped actor)
        {
            Prop prop = incident.ActivityProps.FirstOrDefault(p => p != null && p.Exists());
            if (prop == null || actor == null || !actor.Exists()) return;
            Function.Call(Hash.SET_ENTITY_VISIBLE, prop, true, false);
            if (Function.Call<bool>(Hash.IS_ENTITY_ATTACHED_TO_ENTITY, prop, actor)) return;
            Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY, prop, actor,
                Function.Call<int>(Hash.GET_PED_BONE_INDEX, actor, 28422),
                0.05f, 0f, 0f, 0f, 0f, 0f, false, false, false, false, 2, true);
        }

        private bool EnsureDispatchCamera(LSPDDispatchEvent incident, Ped actor)
        {
            if (incident.ActivityProps.Any(p => p != null && p.Exists())) return true;
            var model = new Model("prop_pap_camera_01");
            if (!model.IsValid || !model.IsInCdImage) return false;
            model.Request();
            if (!model.IsLoaded) return false;
            Prop prop = World.CreateProp(model, actor.Position, false, false);
            model.MarkAsNoLongerNeeded();
            if (prop == null || !prop.Exists()) return false;
            Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY, prop, actor,
                Function.Call<int>(Hash.GET_PED_BONE_INDEX, actor, 28422),
                0f, 0f, 0f, 0f, 0f, 0f, false, false, false, false, 2, true);
            incident.ActivityProps.Add(prop);
            return true;
        }

        internal void ReleasePreparedDispatchActivity(LSPDDispatchEvent incident)
        {
            if (incident == null) return;
            foreach (Prop prop in incident.ActivityProps)
                if (prop != null && prop.Exists())
                {
                    Function.Call(Hash.DETACH_ENTITY, prop, true, true);
                    prop.MarkAsNoLongerNeeded();
                }
            incident.ActivityProps.Clear();
            if (incident.PreparedActivityStarted && incident.OwnedByDispatch)
                foreach (Ped actor in incident.Suspects.Where(p => p != null && p.Exists() && !p.IsDead
                    && !incident.ConvoyCustodySuspectHandles.Contains(p.Handle)
                    && !incident.ArrestedSuspects.Any(a => a != null && a.Handle == p.Handle)))
                {
                    actor.IsPositionFrozen = false;
                    actor.BlockPermanentEvents = false;
                    Function.Call(Hash.TASK_WANDER_STANDARD, actor, 10f, 10);
                }
        }

```

## ResponseAudio.xml

Changed code beginning at line 908:

```xml
  <!-- No new WAVs are fabricated. These semantic aliases reuse existing recordings.
       Specialized wording remains a recording gap and is logged once when used. -->
  <IncidentAliases>
    <Alias type="stalking" recordingType="disturbance" />
    <Alias type="lying_naked" recordingType="disturbance" />
    <Alias type="people_theft" recordingType="disturbance" />
    <Alias type="driver_assault" recordingType="violent_disturbance" />
    <Alias type="ramming" recordingType="reckless_driver" />
    <Alias type="slow_driving" recordingType="reckless_driver" />
    <Alias type="massacre" recordingType="violent_disturbance" />
    <Alias type="interior_dealing" recordingType="drug_activity" />
    <Alias type="interior_gathering" recordingType="criminal_gathering" />
  </IncidentAliases>
  <Group id="dispatch_activity_expansion">
    <Event id="lsimmersivelife.police.scene.activity_confirmed" speaker="dispatch" priority="Dispatch" cooldownMs="45000" expiresMs="15000" reuseEvent="lsimmersivelife.police.dispatch.scene_active" />
  </Group>
```

## CloudValidation.proj

```xml
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <!-- Optional cross-platform source validation. Keep third-party reference
       assemblies outside the repository and provide CloudReferenceRoot. -->
  <PropertyGroup>
    <CloudReferenceRoot Condition="'$(CloudReferenceRoot)' == ''">$(MSBuildProjectDirectory)/BuildReferences</CloudReferenceRoot>
    <FrameworkPathOverride>$(CloudReferenceRoot)/net48/build/.NETFramework/v4.8</FrameworkPathOverride>
  </PropertyGroup>
  <Import Project="LSImmersiveLife.csproj" />
  <PropertyGroup>
    <GTAVEnhancedRoot>$(MSBuildProjectDirectory)/obj/NoGame</GTAVEnhancedRoot>
  </PropertyGroup>
  <Target Name="ProvideCloudValidationReferences" BeforeTargets="CoreCompile">
    <ItemGroup>
      <ReferencePathWithRefAssemblies Include="$(CloudReferenceRoot)/net48/build/.NETFramework/v4.8/*.dll" Exclude="$(CloudReferenceRoot)/net48/build/.NETFramework/v4.8/System.EnterpriseServices.Wrapper.dll;$(CloudReferenceRoot)/net48/build/.NETFramework/v4.8/System.EnterpriseServices.Thunk.dll" />
      <ReferencePathWithRefAssemblies Include="$(CloudReferenceRoot)/references/*.dll" />
    </ItemGroup>
  </Target>
</Project>
```

## Tools/DeployDispatchExpansion.ps1

```powershell
param(
    [Parameter(Mandatory=$true)][string]$GameRoot,
    [Parameter(Mandatory=$true)][string]$PayloadRoot,
    [string]$BackupRoot = (Join-Path $env:TEMP ('LSImmersiveLife-Dispatch-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path (Join-Path $GameRoot 'GTA5_Enhanced.exe'))) { throw 'GTA V Enhanced executable was not found at GameRoot.' }
if (Get-Process -Name 'GTA5_Enhanced','GTA5' -ErrorAction SilentlyContinue) { throw 'Close GTA before deploying.' }
$scripts = Join-Path $GameRoot 'scripts'
$data = Join-Path $scripts 'LSImmersiveLife'
$files = @('LSImmersiveLife.dll', 'LSImmersiveLife/XML/LSPDDispatchEvent.xml', 'LSImmersiveLife/XML/LSPDCriminalProfile.xml', 'LSImmersiveLife/ResponseAudio.xml')
foreach ($file in $files) {
    if (!(Test-Path (Join-Path $PayloadRoot $file))) { throw "Payload file missing: $file" }
}
# Back up every destination before the first replacement. No source/catalog
# is removed; shared SHVDN, LemonUI, NAudio and saved Police profiles stay local.
foreach ($file in ($files + @('LSImmersiveMainUI.xml'))) {
    $existing = Join-Path $scripts $file
    if (Test-Path $existing) {
        $backup = Join-Path $BackupRoot $file
        New-Item -ItemType Directory -Force -Path (Split-Path $backup) | Out-Null
        Copy-Item -LiteralPath $existing -Destination $backup
    }
}
function Load-Xml([string]$Path) {
    $doc = New-Object System.Xml.XmlDocument
    $doc.PreserveWhitespace = $true
    $doc.XmlResolver = $null
    $doc.Load($Path)
    return ,$doc
}
function Merge-Nodes($Destination, $Source, [string]$ParentPath, [string]$ChildName, [string[]]$OnlyIds = @()) {
    $destParent = $Destination.SelectSingleNode($ParentPath)
    $sourceParent = $Source.SelectSingleNode($ParentPath)
    if ($null -eq $destParent -or $null -eq $sourceParent) { throw "Unexpected XML structure: $ParentPath" }
    foreach ($node in $sourceParent.SelectNodes($ChildName)) {
        $id = $node.GetAttribute('id')
        if ($OnlyIds.Count -gt 0 -and $id -notin $OnlyIds) { continue }
        $old = @($destParent.SelectNodes($ChildName)) | Where-Object { $_.GetAttribute('id') -eq $id } | Select-Object -First 1
        $new = $Destination.ImportNode($node, $true)
        if ($null -ne $old) { [void]$destParent.ReplaceChild($new, $old) }
        else { [void]$destParent.AppendChild($new) }
    }
}
# Stage merged data in the backup directory; validate every merge before copying.
$staging = Join-Path $BackupRoot 'merged-payload'
New-Item -ItemType Directory -Force -Path $staging | Out-Null
foreach ($file in $files) {
    $sourcePath = Join-Path $PayloadRoot $file
    $target = Join-Path $scripts $file
    $staged = Join-Path $staging $file
    New-Item -ItemType Directory -Force -Path (Split-Path $staged) | Out-Null
    if ($file -like '*.dll' -or !(Test-Path $target)) { Copy-Item $sourcePath $staged; continue }
    $dest = Load-Xml $target
    $source = Load-Xml $sourcePath
    if ($file -like '*LSPDDispatchEvent.xml') {
        Merge-Nodes $dest $source '/LSPDDispatchEventCatalog/Events' 'Event'
        Merge-Nodes $dest $source '/LSPDDispatchEventCatalog/Branches' 'Branch'
    } elseif ($file -like '*LSPDCriminalProfile.xml') {
        Merge-Nodes $dest $source '/LSPDCriminalProfiles/Profiles' 'Profile' @('dispatch_naked_subject','dispatch_knife_assailant')
        Merge-Nodes $dest $source '/LSPDCriminalProfiles/AssetPools' 'Pool' @('dispatch_naked_peds','dispatch_knife_weapons')
    } else {
        Merge-Nodes $dest $source '/LSImmersiveLifeAudio' 'Group' @('dispatch_activity_expansion')
        $oldAliases = $dest.SelectSingleNode('/LSImmersiveLifeAudio/IncidentAliases')
        if ($null -eq $oldAliases) { [void]$dest.DocumentElement.AppendChild($dest.CreateElement('IncidentAliases')) }
        # Alias identity uses type, so merge without replacing unrelated aliases.
        foreach ($alias in $source.SelectNodes('/LSImmersiveLifeAudio/IncidentAliases/Alias')) {
            $parent = $dest.SelectSingleNode('/LSImmersiveLifeAudio/IncidentAliases')
            $old = @($parent.SelectNodes('Alias')) | Where-Object { $_.GetAttribute('type') -eq $alias.GetAttribute('type') } | Select-Object -First 1
            $new = $dest.ImportNode($alias,$true)
            if ($null -ne $old) { [void]$parent.ReplaceChild($new,$old) } else { [void]$parent.AppendChild($new) }
        }
    }
    $dest.Save($staged)
}
$configPath = Join-Path $scripts 'LSImmersiveMainUI.xml'
if (Test-Path $configPath) {
    $config = Load-Xml $configPath
    $dispatch = $config.SelectSingleNode('//Police/Dispatch')
    if ($null -eq $dispatch) { throw 'Saved Dispatch settings were not found; update maximumSuspects to 5 manually before deploying.' }
    if ([int]$dispatch.GetAttribute('maximumSuspects') -lt 5) { $dispatch.SetAttribute('maximumSuspects','5') }
    $config.Save((Join-Path $staging 'LSImmersiveMainUI.xml'))
}
foreach ($file in ($files + @('LSImmersiveMainUI.xml'))) {
    $staged = Join-Path $staging $file
    if (!(Test-Path $staged)) { continue }
    $target = Join-Path $scripts $file
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Copy-Item -LiteralPath $staged -Destination $target -Force
}
Write-Host "Dispatch payload deployed. Backup and merged files: $BackupRoot"
Write-Host 'GTA observation is still required. No shared dependencies, location data, Crime Activity, Convoy source or saved Profile was replaced.'
```
