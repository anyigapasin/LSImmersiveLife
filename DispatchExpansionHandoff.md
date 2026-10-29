# Dispatch expansion handoff — 2026-10-29

Repository: `github.com/anyigapasin/LSImmersiveLife`.
Base: `738c25e2cdfa9def79442407daf788f87e995083` (`master` at checkout).
Source branch: `codex/dispatch-one-to-five-active-scenes`.
Local workspace: `/workspace/LSImmersiveLife`.

## Observation and implementation

The original preflight hides/freezes prepared actors and starts behavior after
acceptance. The owner clarified that this timing is acceptable; it is retained.
The relevant gaps were three-suspect interior/default limits, an extra-civilian
restriction to retail interiors, foot spawning of vehicle passengers, recurring
identical role poses, no physical confirmation for the expanded activities,
a three-target Backup pursuit cap, and group-wide transport completion.

Read the current Implementation Status, Developer workflow, official Project
Mapping, Gameplay Flow, Gameplay Branch and Gameplay Audio Branch. Gameplay
Review and Gameplay Test DOCX files in this GitHub snapshot are empty and could
not supply additional evidence. Historical local-Windows build and GTA reports
in Implementation Status are not evidence for this cloud build.

The catalog retains all 104 existing IDs and adds 17 missing definitions (121
total). [DispatchExpansionCatalog.md](DispatchExpansionCatalog.md) lists every
eligible event by one, two–three, and four–five suspects, with its activity and
civilian requirements. Compatible existing entries are widened; explicitly
fixed-count variants retain their counts and IDs.

| Requested categories | Connected physical behavior |
|---|---|
| Knife/gun/physical assault; driver assault | Intended suspect/victim combat; driver scene includes a catalog vehicle; gunfire requires an observed shot for activity confirmation. |
| Camera, drunkard, lying naked, stalking | Existing camera/beer disturbance variants retained; validated animation resources; catalog adult actor for lying; following and walking tasks for stalking. |
| Interior store/bank heist, jacking, theft | Existing mapped interior/door owner, clerk/staff and customer pools; robber, accomplice and lookout roles; theft approach, wallet/snatch gesture and subsequent movement. Existing hostage/snatch lifecycle retained. |
| Murder, group murder, massacre | Authored dead victims, moving suspects and reacting survivors. Massacre definitions reserve two additional deceased civilian slots (three victims total); each examination is recorded separately with the existing I-key/range/interior guards. |
| Alley money/gun dealing, meeting, group gathering | Giver/receiver clips, visible cash or catalog weapon prop transfer, conversation and moving lookout roles; separate witnesses. |
| Group assault, lively-place chaos, store assault, threat gathering | Distinct attackers, witnesses, cowering civilians, movement and individual encounter reactions. |
| High speed, slow driving, ramming, two-car chaos | Real driver/passenger seats, driveability/engine/brake/road checks and actual progress sampling. Both cars participate. Ramming confirmation additionally requires the target to have been damaged by the assigned vehicle. Moving callout tracking follows the same actors. |

`LSPDDispatchEvent.cs` reads and validates `preparedActivity` and
`additionalDeadVictims` through the existing selection path. AmbientWorld
establishes roles and samples only occurrence participants every 750 ms. An
assigned task alone is insufficient: animation playback, displacement, combat
against the intended victim, observed shooting, real vehicle progress or
verified ramming damage supplies evidence. Stalled initial activity gets two
targeted retries, then a recoverable failure through the existing lifecycle.
Encounter, injury, cuffing, Backup and Convoy tasks have priority over ambient
scene tasks. There is no new gameplay controller and no actor respawn after
acceptance.

The default/config catalog supports five suspects. Civilian slots support five
independently of the suspect roster. Backup can track five foot pursuit targets
through its existing officer assignment owner. Dispatch resolves only physically
transferred custody members; remaining suspects or investigation retain the
incident. Existing seat-based Convoy capacity, recoverable transport failure,
Utility/van selection, physical arrest, Station receipt and Prison continuation
remain in their existing owners. No Convoy or Crime Activity source was changed.

Accepted authored scenes use distance-based cleanup. Convoy-owned suspects are
excluded from Dispatch's deferred cleanup; borrowed civilians return through the
existing claim-release path. Optional props are detached/released, unsecured
suspects receive departure behavior, and accepted-scene entities remain until
the Player leaves the cleanup radius. Unaccepted hidden preparation still uses
the original failure cleanup.

## Audio

`LSPDAudioDispatch.cs` reads semantic recording aliases and validated same-speaker
recording reuse from `ResponseAudio.xml`. Activity-confirmed radio is emitted
only after observed activity. Existing occurrence/transition duplicate
suppression and terminal scope cancellation remain active. Initial report,
accept/decline, approach, arrival, investigation, fleeing, resistance, gunfire,
Backup, surrender, custody, transport and completion continue through Dispatch's
existing report boundaries. No new playable WAV was invented.

Dedicated wording for stalking, lying naked, pedestrian theft, driver assault,
ramming, slow driving, massacre and interior social scenes uses the authored
fallback meanings and logs a missing-specialized-recording notice once.
The repository also lacks 14 previously cataloged ready WAVs: seven under
`Police/en-US/backup/` (requested 01/02, additional_requested, multiple_requested,
supervisor_requested, immediate_requested, cancelled) and seven under
`Police/en-US/backup_reply/` (acknowledged 01/02, assigned, unavailable, denied,
cancelled, released). These existing files are not included or fabricated.

## Validation and build

Bounded source milestones 1–7 compiled the complete production source list for
.NET Framework 4.8, C# 7.3, x64. Final validation also builds the imported original
production project with MSBuild in Release/x64. Third-party reference assemblies
are external: SHVDN nightly 3.7.0-nightly.195, LemonUI 2.2 and NAudio 1.10.0;
Microsoft's net48 reference assemblies supply the target framework. The cloud
reference set is not the owner's locally installed Enhanced dependency set.
Warnings concern existing/deprecated API usage under this newer reference set;
see the packaged final build log for exact warnings and zero-error evidence.

Checks passed:

- Real C# parsing of all 121 definitions, 80 production suspect-count samples
  per event, including selection of four/five where authored; invalid activity
  rejection.
- Production audio alias resolution, actual existing-file availability,
  occurrence duplicate suppression and closed-scope rejection; no audio device
  or game was opened.
- XML profile/pool/branch references, preservation of original IDs, no newly
  nonexistent WAV paths, and unchanged Crime/Convoy/location sources.
- `git diff --check` and complete source build. No GTA runtime test was performed.

## Deployment and remaining GTA observation

GitHub publication supplies the source branch, edited-source archive and built
runtime package. No local GTA installation exists here, so live game deployment
has not occurred. `Tools/DeployDispatchExpansion.ps1` performs the Windows
installation with GTA closed, backs up destinations first, merges the authored
Dispatch entries and only the new criminal/audio records, and updates only the
saved Dispatch suspect-limit attribute when necessary. It preserves customized
unrelated catalog nodes, saved profiles, locations and shared dependencies.
Its Windows installation run still needs observation.

From the extracted runtime package on the game PC:

```powershell
powershell -ExecutionPolicy Bypass -File .\Tools\DeployDispatchExpansion.ps1 `
  -GameRoot 'C:\path\to\Grand Theft Auto V Enhanced' -PayloadRoot '.\scripts'
```

Observe these exact scenarios before treating this as gameplay-confirmed:

1. Accept each action family and approach normally: stalking follows a moving
   victim; exchange props/clips align; gun/knife/melee attacks target the victim;
   naked actor/lying animation and camera/drunk variants stream and look correct.
2. Four/five suspects plus all staff/customers in a small accessible store and
   bank: floor/door access, crowd spacing, distinct roles and per-suspect mixed
   flight/resistance/compliance. Failed small-site preparation must choose a
   compatible alternative without reducing the required roster.
3. Three deceased massacre victims: examine each separately with I; transport
   and completion must wait for the last examination. Check survivors react.
4. One-car groups with driver/passengers; slow, fast and real ramming contact;
   both cars in the two-car event. Verify progress, moving markers, collision
   stop, dismount, independent foot reactions and bounded stalled-route recovery.
5. Secure/release/neutralize one member while another flees; partially transfer
   custody, finish that transport and continue the unresolved scene. Test full
   five-person custody against actual seats and unavailable-van recovery.
6. Preserve known timing, investigation controls, wounded-suspect protection,
   Utility/van fallback, physical escort/load/unload, selected Station receipt
   and existing Prison transport continuation. Cancel/decline/expire scenes near
   and far from the Player and confirm no stranded frozen actors or claims.

These are source/build results and observation requirements, not GTA passes.
