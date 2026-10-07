# LS Immersive Life Developer / Runtime Inspection workflow

Developer Mode is a passive inspection layer over the existing gameplay. It is
not a second Police architecture and it does not become an owner of NPCs,
vehicles, Dispatch, Backup, Convoy, Police Response, Crime Activity, or the
Universal UI. Opening the menu does not start, stop, reset, or retask gameplay.

It never spawns, deletes, teleports, arrests, cuffs, escorts, forces Backup,
forces Dispatch, changes NPC decisions, replaces AI tasks, changes Police
Authority, or changes custody decisions. It reports evidence and leaves the
gameplay owner responsible for any fix.

## Where it lives

- Observer and analyzer: `LSImmersiveDeveloper.cs`
- Developer configuration: `scripts\\LSImmersiveLife\\Plugin\\LSDeveloper.xml`
- Detailed evidence: `scripts\\LSImmersiveLife\\Log\\LSDeveloperTrace.log`
- Existing correlation records: `LSRuntime.log` and `LSDebug.log`
- Build payload: `bin\\x64\\Debug\\LSImmersiveLife\\Plugin\\LSDeveloper.xml` and the matching Release directory

`LSDeveloper.xml` is separate from every Police gameplay configuration. Its
reload action reloads only Developer settings; it does not reload or reset
Dispatch, NPC Response, Backup, Convoy, Crime Activity, or Police Authority.

## One controlled gameplay test

1. Launch GTA V and enter Police Authority normally.
2. Enter Patrol normally if the selected scenario needs Patrol.
3. Open the existing Universal UI and open **Developer**.
4. Select one branch: Foot citizen document rejection, Vehicle citizen document rejection, Backup request, Fleeing citizen interception, or Escort to Police vehicle.
5. Choose **Start Test**.
6. Choose **Capture Checkpoint** before performing the controlled action.
7. Perform exactly one scenario. Do not combine a document test with a second Backup or Convoy test.
8. Stop when the behavior first becomes wrong. Do not keep trying to repair the scene manually.
9. Choose **Capture Checkpoint** again, then **End Test**.
10. Choose **Analyze Current Test**. The result is written to `LSDeveloperTrace.log` and summarized in the normal Runtime/Debug logs.

The analyzer compares the expected branch sequence to the observed timeline and
reports the earliest serious failure, task conflict, physical mismatch, or
unconfirmed expected step. It does not claim that the final error is the root
cause and it does not claim a bug was fixed.

## What is captured

The runtime keeps a bounded circular buffer in memory. In Test mode it observes
only while a Developer test is active. In Always mode it keeps the same bounded
buffer during ordinary gameplay. It does not write a line every frame and it
does not scan every GTA Ped.

During a test, sampling is limited to actors already exposed by the existing
event stream: the active actor, target, assigned vehicle, Backup/Convoy actors,
the player when relevant, and other directly participating handles. Samples can
include existence, model hash, position, speed, vehicle occupancy, target
distance, logical state, the last issued task, raw status for a small set of
known GTA task hashes, selected weapon hash, shooting, combat, aiming-from-cover,
and the physical combat target. A weapon being equipped is not treated as proof
that the ped is aiming; ordinary aiming remains `UNKNOWN` unless GTA exposes a
native signal such as combat, shooting, or aiming-from-cover.

The default sample interval is 750 ms, and the maximum actor set is eight. A
behavioral stall is reported only after the same logical stage and expected
continuation remain without progress for the configured `behavioralStallSeconds`
(default six seconds). The observer does not run these checks every frame, does
not write each sample to disk, and does not replace the task when a stall is
detected. With `showLiveNotifications=true`, the first confirmed stall posts
one compact in-game notification; it does not post a notification for every
sample.

Evidence is flushed only at a checkpoint, test end, first detected anomaly,
serious failure, or an explicit analysis request. The trace buffer, test event
count, and `LSDeveloperTrace.log` are bounded by configuration. The normal
Runtime and Debug logs remain available and are correlated through the runtime
session ID and Developer Test ID.

The report separates:

- `CONFIRMED FACT`: a record or native observation actually exposed it;
- `OBSERVED STATE`: a sampled logical/physical state;
- `EVENT HISTORY`: the ordered bounded timeline;
- `INFERENCE`: a conclusion derived from that evidence;
- `UNKNOWN`: information the runtime did not expose;
- `FINAL CLASSIFICATION`: the diagnostic result for this test only.

Important distinctions include asset preparation, model invalidity/loading,
vehicle availability, route failure, task recovery, state-transition failure,
ownership conflict, target invalidation, physical mismatch, custody failure,
cleanup failure, and terminal completion failure.

## Configuration bounds

The shipped configuration is intentionally conservative for a 16 GB laptop:

```xml
<LSDeveloper version="2.0"
  enabled="true"
  developerMenuEnabled="true"
  automaticRuntimeWatch="false"
  automaticAnomalyDetection="true"
  liveBehavioralConfirmation="true"
  traceMode="Test"
  samplingIntervalMilliseconds="750"
  behavioralStallSeconds="6"
  traceBufferSeconds="30"
  maximumTraceEvents="800"
  maximumTraceFileKilobytes="2048"
  preFailureCaptureSeconds="8"
  postFailureCaptureSeconds="5"
  showLiveNotifications="true"
  showDetailedNotifications="false"
  writeLifecycleMarkers="true"
  writeTraceFile="true" />
```

Invalid XML values fall back to safe defaults and numeric values are clamped.
`maximumTraceFileKilobytes` limits the dedicated evidence file; Developer Mode
does not let it grow without bounds. These settings do not contain Police,
Dispatch, NPC decision, Backup, or Convoy configuration.

`liveBehavioralConfirmation` controls the low-frequency native observations.
`behavioralStallSeconds` is clamped to 3--30 seconds. Raising the interval or
stall threshold reduces observation work; lowering them makes short stalls more
visible but still never enables per-frame logging.

## Interpreting a result

For example, if the timeline contains document rejection, flee decision,
fleeing task, Backup request, arrival, approach, and route recovery, but no
custody transition, the analyzer should report the first divergence as a
custody transition not being confirmed after a valid interception boundary.
It should then show the actor/target/vehicle handles, owners, state/task
changes, timestamps, physical samples, raw task status when available, the
expected next continuation, and the mapped gameplay source file/member. A
Backup escort stall is reported as a behavioral stall only when the selected
actor remains in the observed logical stage and physical condition; the result
still labels the hidden GTA reason as `UNKNOWN` unless a failure/timeout/rejection
or task-status record proves more.

That finding is a diagnosis. The Developer layer does not repair the Backup or
NPC system and should never be used as proof that gameplay is fixed.
