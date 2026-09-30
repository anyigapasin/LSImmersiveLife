# Anyi's LS Immersive Life Milestone

## September 21, 2026 — The first genuinely satisfying citizen-custody test

Today felt like one of the biggest practical milestones in the Police Mode development so far. The test was not just a menu or a log saying that an operation had started. I was able to play through the citizen interaction, physically arrest the citizen, escort the citizen to my Police Cruiser, load the citizen into the rear seat, drive to Mission Row, unload the citizen, and finish the custody handoff.

The session began normally. Police Authority opened, my saved Police profile and personal equipment loaded, Mission Row was selected as the active station, and the Police world was placed into duty mode. The normal Police protection was active, the unwanted vanilla Police dispatch behavior was suppressed, and the audio system loaded normally. The criminal catalog and the citizen database also loaded correctly before Patrol began.

I selected the LSPD Police Cruiser and started Patrol. Patrol immediately prepared the vehicle and produced a Dispatch offer. The offer was a Strawberry carjacking call, but I declined it so I could continue testing the nearby citizen interaction. That was useful because the game returned naturally to Patrol instead of trapping the player inside the declined call.

The first nearby foot-citizen attempt reached the real interaction flow. The citizen was noticed, greeted, and checked. The database assigned Nicholas Hayden, citizen record LSC-2026-EAS-000001. His record identified him as a taxi driver from East Los Santos with a Community Contact status and no warrant. After the documentation decision, the citizen complied with hands raised, and I performed the physical arrest. The arrest began and completed through the actual Police arrest interaction.

That first custody attempt did not finish because the citizen died during the early escort phase. The runtime recorded the real reason as the prisoner dying, rather than pretending that the person had simply vanished or inventing a lost-custody message. The operation then closed cleanly. It was still a useful part of the test because it proved that the custody owner recognized the actual failure and did not leave a fake Convoy running forever.

The next citizen interaction was the successful one. I approached another foot citizen and the interaction behaved properly: the citizen greeted me, documents became available, and the citizen record was assigned. This record belonged to Mateo Douglas, citizen record LSC-2026-MIR-000003. The database identified him as a registered nurse from Mirror Park with a Registration Lapse status and no warrant. The record carried a Medical Card that was current, and the recommendation correctly kept the vehicle issue separate from the person's identity.

I rejected the validation outcome, the citizen complied with hands raised, and I performed the physical arrest. This time the complete custody flow continued correctly.

Because I was using the Police Cruiser rather than a Police motorcycle, the system recognized that I could personally transport one arrested citizen. The citizen was transferred from the NPC interaction owner into the existing Convoy custody flow. The player remained the arresting and escorting officer. The citizen did not become a normal pedestrian again, did not wander away, and did not independently decide to enter the Police vehicle.

The physical escort began beside the Cruiser. The citizen stayed attached to the player-controlled escort, used the restrained prisoner movement behavior, and moved with me toward the rear passenger door. When the rear-door position was reached, the new prompt appeared. I pressed E to open the rear door, then pressed E again to load the citizen. The rear left door opened, the physical loading interaction began, the escort was released only at the correct loading point, and the citizen entered the rear seat through the normal GTA vehicle-entry task.

The important moment was that the log confirmed the citizen as physically loaded rather than merely marked as transported. The door closed, the vehicle became ready for player transport, and the state changed to driving to Mission Row. This is the first test where the whole player-owned Cruiser section behaved like an actual police custody procedure instead of a citizen casually walking to a car.

I drove the Cruiser to Mission Row with the citizen secured inside. At the station, the operation changed into the station-unloading phase. The station-side Police officer was present and ready to receive the citizen. I opened the rear door and started the unload interaction. The citizen physically left the Cruiser while still under custody, the rear door closed, and the receiving officer took over the handoff. The station handoff was confirmed, the custody entered the completed state, and the citizen interaction finished. Prison transfer was declined, so the case ended at the station and I was free to continue Police activity.

The full successful sequence was therefore:

Police Authority → Patrol → citizen noticed → greeting → documents → citizen complies → player arrest → physical escort → rear door opened → prisoner loaded → player drives to Mission Row → rear door opened at station → prisoner unloaded → station officer receives the citizen → custody completed → player returns to duty.

The same day also produced a separate Backup milestone. During the earlier test, a fleeing citizen was tracked by the Backup unit instead of being treated as a stationary marker. The Backup unit reached the fleeing subject, physically contained the subject, presented the custody response, and accepted the custody decision. The Backup officer then escorted the citizen to the Backup vehicle, opened the rear door, and loaded the citizen. Backup transport started, the player's NPC session was released immediately, and the Backup vehicle departed the player's scene. The background departure completed within its bounded window, after which the Backup operation cleaned itself up. This means the player was able to continue Police activity without being forced to follow the Backup car all the way to the station.

That Backup result and the successful Police Cruiser result now show two different but coherent custody choices. When I have a suitable Police car, I can personally escort and transport the citizen. When Backup owns the handoff, Backup can load the citizen and leave while I continue my duty. Both paths now have a clear moment where responsibility changes and the player is not left waiting on an endless invisible operation.

The wider runtime also remained active around the test. A Dispatch offer was created and declined cleanly, traffic vehicles successfully pulled over during later patrol moments, a Crime Activity lead appeared and expired without becoming an unwanted automatic mission, and a vanilla gang incident started and resolved. Those events did not prevent the successful citizen custody run.

This is the milestone I want to preserve as the current stable baseline. The most important achievement is not simply that a citizen entered a Police car. It is that the player could feel the whole chain: approach, decision, compliance, arrest, physical escort, rear-door interaction, loading, driving, unloading, receiving officer, and completion. The first death during the earlier attempt remains a separate edge case to investigate later, but it did not compromise the successful second custody run.

Today's test proves that citizen interaction and personal Police Cruiser transport can now use the same physical custody feeling as the stable Dispatch Convoy path, while Backup can operate as its own background Police duty after the citizen has been loaded.
