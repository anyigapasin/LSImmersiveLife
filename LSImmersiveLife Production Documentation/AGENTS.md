# LSImmersiveLife Agent Instructions

# LSImmersiveLife – Codex Agent File Map

This map defines which file owns each responsibility.

Before changing code:

1. Identify the existing owner.
2. Read that owner and its direct callers.
3. Extend the existing owner.
4. Do not create another controller for the same behavior.
5. Do not move gameplay logic into XML, UI, logging, or PoliceCore.
6. Preserve the documented Gameplay Flow and Gameplay Branches.

Main Dispatch ownership flow:

LSPDDispatchEvent.xml
→ LSPDDispatchEvent.cs
→ LSPDDispatch.cs
→ LSWorldAmbientBehavior.cs
→ LSPDBackUp.cs when support is requested
→ LSPDConvoy.cs when custody transport begins

## Universal files

- `LSIMMERSIVEPATH.cs` — Central paths for configuration, logs, audio, plugins, and Police data.
- `LSImmersiveDeveloper.cs` — Developer Mode, controlled tests, branch inspection, and anomaly observation.
- `LSMainSetting.cs` — In-game settings interface. It edits values owned by MainConfig.
- `LSImmersiveMainConfig.cs` — Loads, stores, and supplies central configuration and controls.
- `LSImmersiveLog.cs` — Writes `LSRuntime.log` and `LSDebug.log`. Logging only; never gameplay.
- `LSRoleplay.cs` — Selects the Player’s role and passes it to AuthorityRole.
- `LSAuthorityRole.cs` — Main interface for the selected role.
- `LSImmersiveHotkeys.cs` — Reads and routes configured controls. It does not own gameplay.
- `LSImmersiveDictionaryAnimation.cs` — Validates and plays shared animation clips. The calling gameplay file remains the owner.

## Police framework

- `LSImmersivePoliceUI.cs` — Police UI and Player selections only.
- `LSImmersivePoliceCore.cs` — Creates, connects, and coordinates Police owners. Do not place actors or duplicate owner state machines here.
- `LSPDProfile.cs` — Police Profile, preferred Ped, vehicle, weapons, inventory, and Backup preferences.
- `LSPDStation.cs` — Starting Police framework: station, wardrobe, garage, armory, agency, profile setup, and station interface.
- `LSPDPatrol.cs` — Active Patrol shift, Patrol continuation, and Personal Utility recovery.
- `LSPDAuthority.cs` — Police Authority state, ally recognition, wanted/hostility conditions, and protection from inappropriate reaction to Police player.
- `LSPDPoliceResponse.cs` — Ordinary Police-to-Police support around the active Authority reaction and in-game police response around the Player and controllable and suited police response toward the player in the Police Duty. This is separate from requested Backup.

## Dispatch ownership

- `LSPDDispatchEvent.cs` — Reads and validates Dispatch definitions, branches, roles, participants, asset references, location requirements, and audio references. It does not physically spawn the scene.
- `LSPDDispatch.cs` — Owns the live Dispatch Activity: scheduling, selection, offer, accept/decline, GPS, arrival, encounter, suspect response, investigation, arrest, and completion.
- `LSWorldAmbientBehavior.cs` — Current physical scene-preparation owner: validates locations, prepares actors/assets/props, checks interiors and access, and starts initial ambient behavior.
- `LSImmersiveLocationCatalog.cs` — Reads and supplies authored location records. It does not decide final live physical usability.
- `LSPDAudioDispatch.cs` — Resolves and plays audio for facts reported by gameplay owners. Audio must not invent gameplay events.

## Police support and custody

- `LSPDBackUp.cs` — Requested supporting force: units, officers, vehicles, staging, arrival, pursuit, interception, containment, and group assistance.
- `LSPDConvoy.cs` — Custody transport owner: Transport Officers, escort, driver, standby officers, loading, unloading, station handoff, prison continuation, and transport completion.

Ownership rule:

- Dispatch retains the incident.
- NPC Response retains the Citizen interaction.
- Backup assists the current owner.
- Convoy takes custody only when a valid transport handoff begins.
- PoliceCore coordinates these owners but does not replace them.

## Citizen Interaction

- `LSPDNPCResponse.cs` — Owns Foot and Vehicle Citizen interaction, validation response, acceptance/rejection, compliance, aggression, flight, pursuit connection, arrest connection, and ambient traffic protection.
- `LSNPCDatabase.cs` — Supplies stable Citizen identity, documents, warning/bad status, warrants, and record display. It does not control the Citizen physically.

## Gang systems

- `LSPDGangDataIntegration.cs` — Read-only connection to external Gang identity and territory data.
- `LSPDVanillaGangResponse.cs` — Observes vanilla Gang identity, friendliness, combat, and threats. Observation is not proof that this project started an attack.
- `LSPDGangAdapterResponse.cs` — Owns project-started Gang responses only after confirmed aggression and no conflicting gameplay owner.

Crime Activity is not Dispatch. Do not edit it unless the request explicitly includes Crime Activity.

## XML ownership

- `LSImmersiveMainUI.xml` — Central settings and control configuration.
- `ResponseAudio.xml` — Audio catalog only.
- `LSPDDispatchEvent.xml` — Dispatch incident definitions, roles, branches, participants, assets, location requirements, and audio references.
- `LSPDImmersiveProfile.xml` — Saved Police Profile and Backup preferences.
- `LSPolicePersonalWeapons.xml` — Saved personal weapon loadouts.
- `LSPoliceWeaponData.xml` — Shared Police weapon catalog.
- `LSPoliceVehicle.xml` — Shared Police vehicle catalog.
- `LSPoliceModelPed.xml` — Shared Police officer and Authority models.
- `LSPDImmersiveUtility.xml` — Agencies, stations, garages, parking, custody, and prison-transfer references.
- `LSPDCriminalProfile.xml` — Criminal Ped, weapon, vehicle, and reusable criminal asset definitions.
- `LSNPCDatabase.xml` — Citizen identity, documents, status, and warrant data.
- `LSImmersiveLocation.xml` — Shared exterior, entrance, interior, vehicle, height, and access coordinates.
- `LSDeveloper.xml` — Developer Mode inspection settings.

XML supplies data. C# owners validate and perform gameplay. Never put AI tasks or runtime state machines inside XML.

## Important boundaries

- Event selection and authored branches: `LSPDDispatchEvent.cs`
- Live Dispatch state: `LSPDDispatch.cs`
- Physical scene preparation: `LSWorldAmbientBehavior.cs`
- Requested Police assistance: `LSPDBackUp.cs`
- Custody and transportation: `LSPDConvoy.cs`
- Citizen interaction: `LSPDNPCResponse.cs`
- Citizen records: `LSNPCDatabase.cs`
- Coordination only: `LSImmersivePoliceCore.cs`
- Audio only: `LSPDAudioDispatch.cs`
- Logging only: `LSImmersiveLog.cs`
- UI only: Police UI, settings, profile, and role interfaces

## Historical names—do not recreate them

- `LSWorldAmbientWorld.cs` and `LSWorldAmbient.cs` refer to the current `LSWorldAmbientBehavior.cs` responsibility.
- `LSPDDispatchAudio.cs` refers to the current `LSPDAudioDispatch.cs`.
- `LSPDModelPed.xml` refers to `LSPoliceModelPed.xml`.
- `LSImmersivePoliceProfile.xml` refers to `LSPDImmersiveProfile.xml`.

Do not create duplicate files using these historical names.

## Codex change rule

For every requested fix, report:

- Observed gameplay problem.
- Runtime evidence.
- Existing owner file.
- Direct callers affected.
- Exact behavior changed.
- What was deliberately left unchanged.
- Source validation completed.
- Build identity.


`IMPLEMENTATION_STATUS.md` records work and tests. It is not allowed to override the official Mapping, Gameplay Flow, or Gameplay Branch documents.

`Mishandled Crash.md` records mishanded code and errorr environment and project's crash environment. It is not allowed to override the official Mapping, Gameplay Flow, or Gameplay Branch documents.

## Required ownership route

Use this route when tracing or adding behavior:

`UI or hotkey -> PoliceCore coordination -> existing gameplay owner ->
WorldAmbient placement when required -> data catalog -> Audio and Log reporting`

Examples:

- Dispatch offer or incident progression -> `LSPDDispatch.cs`
- Dispatch definition or asset reference -> `LSPDDispatchEvent.cs` and XML
- Scene placement, interior, doorway or ground validation -> `LSWorldAmbientBehavior.cs`
- Location record interpretation -> `LSImmersiveLocationCatalog.cs`
- Player-requested Police assistance -> `LSPDBackUp.cs`
- Ordinary Police response -> `LSPDPoliceResponse.cs`
- Arrested-suspect transport -> `LSPDConvoy.cs`
- Citizen or traffic interaction -> `LSPDNPCResponse.cs`
- Citizen identity/status -> `LSNPCDatabase.cs`
- Processing order or activity blocking -> `LSImmersivePoliceCore.cs`
- UI display only -> `LSImmersivePoliceUI.cs`
- Config value -> `LSImmersiveMainConfig.cs` and `LSImmersiveMainUI.xml`
- Audio lookup/playback -> `LSPDAudioDispatch.cs` and `ResponseAudio.xml`
- Runtime evidence -> `LSImmersiveLog.cs`
- Developer observation -> `LSImmersiveDeveloper.cs`

## Duplicate-code prevention

Before adding a method, search the existing owner and its callers.

Do not:

- Create another Dispatch, Backup, Convoy, Citizen or scene controller.
- Put physical gameplay inside UI, PoliceCore, XML, logging or audio.
- Put event definitions directly into C# when the XML catalog owns them.
- Let Dispatch duplicate WorldAmbient placement checks.
- Let WorldAmbient decide Dispatch outcomes.
- Let Convoy generate Dispatch suspects.
- Let Police Response replace player-requested Backup.
- Treat logs, notifications, audio or coordinates as proof of physical completion.
- Edit Crime Activity merely because a Dispatch event involves a crime.

If ownership is unclear, identify the first failing transition and inspect the
existing caller before choosing a file.

## Non-gameplay files

| File or folder | Meaning |
| --- | --- |
| `AGENTS.md` | Development rules and ownership boundaries |
| `IMPLEMENTATION_STATUS.md` | Current evidence, completed milestones and pending GTA checks |
| Official documentation | Intended gameplay and architectural truth |
| `LSRuntime.log` | Recorded gameplay transitions from one loaded build |
| `LSDebug.log` | Errors, rejection reasons and diagnostic evidence |
| `Tests/` | Focused source and regression validators; not gameplay |
| `Tools/`| Sandbox validation/build outputs; not production source |
| `LSImmersiveLife.csproj` | Actual compiled-file and resource manifest |

Always verify that an edited `.cs` file is included in the current
`LSImmersiveLife.csproj`. A file existing in the folder does not prove it is
compiled.

## Mission

Work inside Anyi's existing LSImmersiveLife project. This is an incremental,
document-driven implementation and stabilization task, not a new project or a
permission to redesign the architecture.

Follow the latest explicit user request and the supplied project documents.
Preserve the user's terminology, authored content, existing working gameplay,
file responsibilities, public contracts, XML/audio/profile data, and ownership
boundaries.

Do not invent gameplay, silently reinterpret a documented branch, replace a
stable system with a cleaner one, create a second gameplay controller, or make
unrelated changes merely because they are convenient.

## Scope and exclusions

The normal scope is the documented Dispatch Activity, Backup, Escort/Transport,
custody/Convoy handoff, Citizen Ped Interaction, Police Profile/Station, UI,
authority, patrol, configuration, audio, location catalogs, criminal profiles,
and their documented dependencies.

## Source priority and evidence

Use this order when deciding what to do:

1. The user's latest explicit request and confirmed decisions.
2. The official Mapping, Gameplay Branch, Gameplay Flow, Visual Design, and
   Gameplay Audio documents.
3. The current Visual Studio source, project file, XML, and call paths.
4. Production, runtime, debug, and developer logs as dated evidence.

official Documents define intended behavior. production live sources defines what currently historical produced on the Development.
Successful compilation proves only compilation. A log proves only the behavior
recorded in that log. In-game behavior is confirmed only after the user or the
agent actually observes that exact scenario.

If official documents conflict, show the exact conflict and ask one short,
plain-English question. Do not silently choose a new behavior. Continue any
independent work while the disputed change is held.

## Efficient reading and usage control

At the beginning of a task, inspect the working directory, source-control, solution/project files, and available reference filenames once. Resolve filename prefixes/capitalization differences once; do not invent a
missing document.

Index document headings and source filenames once. Then select one requested
feature or gameplay branch and read only its complete relevant sections,
connected alternatives, terminal states, and the smallest necessary code/log
ranges. Use `rg` or equivalent bounded search and line-range reads.

Do not repeatedly reread every document, dump whole source files, replay old
passing logs, or rescan unrelated branches. Reuse evidence already obtained.
Keep `IMPLEMENTATION_STATUS.md` concise: requirement, source heading or log
range, owner/call path, acceptance scenario, status, evidence, and next step.
Never copy whole documents or logs into it.

When a model is changed, use the existing status and the previous compact
handoff. The new model may verify the affected files and relevant lines, but it
must not restart a full-project analysis without a specific evidence gap.

## ONly if Model routing for efficient development

When the user selects this two-model workflow, use one model switch per bounded
branch, not a switch after every sentence or command:

1. **GPT-6 Luna Max — analysis and validation pass.** Read the relevant official
   document sections, connected branch states, runtime/debug logs, and affected
   source call paths. Run bounded PowerShell/search, baseline, and sandbox/MSBuild
   checks when useful. Produce a compact handoff containing facts, evidence,
   suspected owner, exact files/methods, missing or incorrect logic, acceptance
   criteria, and the checks performed. Luna Max must not implement unrelated
   gameplay or deploy anything.
2. **GPT-5.6 Sol — implementation pass.** Switch once after the Luna Max
   handoff. Verify the affected source and callers against that handoff, then
   implement/debug only the selected branch in the actual Visual Studio source
   project. Put the code change in the project source, not in a new sample or
   detached explanation. Run only the minimum focused sandbox compile/check
   needed to validate the edit; do not repeat Luna Max's full document scan.
3. **Optional Luna Max review.** Use Luna Max again only when the user requests
   an independent final review or the changed branch has a concrete unresolved
   build/runtime question. Review the diff and targeted evidence only; do not
   restart the whole project analysis.

Store the compact handoff in the existing status file or the current chat
context. Do not create extra analysis documents unless the user asks. If Luna
Max's conclusion conflicts with live source, Sol must report the conflict and
correct the bounded implementation from source evidence rather than blindly
following the handoff.

## Required workflow

Use this sequence for each bounded milestone:

`READ -> VERIFY -> IMPLEMENT -> SANDBOX BUILD -> FOCUSED VALIDATE -> HANDOFF`

Before editing, identify in one short message:

- the single branch or defect being handled;
- the relevant documents/sections;
- the actual owners and files;
- the acceptance check.

Implement one branch at a time. Keep a branch's code change, focused build,
and report together. Do not begin a second feature while the current branch has
an unresolved compile or ownership problem unless the user asks for parallel
work.

After each meaningful milestone, update the concise status file and report:
source review, changes, sandbox build/checks, unresolved issues, and the next
manual GTA test. Do not claim a feature is stable, deployed, or complete without
evidence for that exact claim.


## Change discipline

Read the complete affected method and its callers before editing. Prefer a
focused update to the existing owner. Preserve unrelated code, comments,
public APIs, XML records, profiles, audio references, and project includes.

Do not replace a large class with a sample, broadly reformat, upgrade
dependencies, change framework/libraries, move directories, or expand a file
dramatically. The original file scope and readable structure matter. A new
supporting logic file is allowed only when the documents and live call path
prove that the existing foundation lacks that responsibility; explain its owner
and keep the addition minimal.

Never add placeholder logic, fake DLLs, dummy natives, simulated success,
coordinate-only completion, silent teleportation, or deletion of entities not
owned by the system. Preserve physical arrival, door/path interaction, arrest,
handcuff/secure, escort, load/unload, custody handoff, and cleanup where the
documents require them.

Keep the user's confirmed decisions in force, including the first automatic
Dispatch grace period, immediate explicit radio request, documented Release The
Ped ownership, personal-utility/van fallback, and the confirmed stable Convoy
behavior. Do not reopen a confirmed stable branch without a new failure.

## Questions and clarification

Ask only when the answer could change gameplay, ownership, data, deployment,
architecture, or a destructive action. State the exact document/code gap, its
consequence, and one recommended choice in simple English.

Do not ask the user to restate information already present in the supplied
documents, source, status file, or earlier confirmed decisions. Do not ask for
permission for a safe, already-authorized sandbox read/build/check. Do ask
before a disputed implementation or any live deployment.

## Builds and deployment safety

Sandbox/source-project builds and focused checks are approved when their outputs
remain inside the source workspace, a temporary folder, or an explicit sandbox
output folder. MSBuild/Visual Studio validation may run there. Preserve
uncommitted user work and do not upload the user's GTA installation to solve a
missing local runtime.

The default decision for deployment is **DISAPPROVE**. Never copy, install,
load, overwrite, or post-build-deploy DLLs, extensions, XML, configs, scripts,
assets, or compiled outputs into the live GTA `scripts`, `plugins`, `mods`, or
runtime folders. Never replace the scripts folder and never edit a deployed
copy as the source project.

If a build or post-build command targets a live game folder, skip that step,
report it, and keep the artifact in the sandbox. Do not disable this barrier
implicitly. Only a later, explicit user instruction naming the exact payload and
target may reopen deployment review; ask for confirmation immediately before
any copy or overwrite and preserve a backup first.

Report these separately:

- source/static review;
- sandbox build result;
- focused automated checks;
- deployment status (normally `not deployed`);
- manual GTA/in-game validation (normally `pending` here).

An old runtime log or a green build is not proof of current in-game behavior.

## Physical and runtime validation

Trace the first unexpected transition in the log to its owner, occurrence/scene
identity, and timestamp. Validate actual entity validity, ownership, usable
seats, occupants, pathing, animation/task availability, capacity limits,
authority/reset cleanup, repeated inputs, duplicate audio, and missing
resources when relevant.

Completion must depend on the documented physical state, not only proximity, a
notification, audio, or a state flag. If GTA cannot be run or observed, mark
the scenario pending and provide a short user-run test sequence.

Keep Dispatch, Citizen Interaction, Backup, and Convoy as separate contexts.
Do not use an unverified behavior from one context as proof for another.

## Handoff format

Keep the final handoff compact:

1. What changed and why.
2. Files/owners and document or log evidence.
3. Sandbox build and focused checks actually run.
4. What remains pending or failed.
5. Exact next gameplay branch and manual test.

Do not include full documents, whole logs, giant code dumps, or unrelated
refactors in the handoff.

