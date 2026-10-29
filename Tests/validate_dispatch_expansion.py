"""Catalog integration checks; native GTA activity must be observed in-game."""
from pathlib import Path
import xml.etree.ElementTree as ET
import subprocess
root=Path(__file__).resolve().parents[1]
events=list(ET.parse(root/'LSPDDispatchEvent.xml').getroot().iter('Event'))
assert len({e.get('id') for e in events})==len(events)
profiles=ET.parse(root/'LSPDCriminalProfile.xml').getroot()
profile_ids={e.get('id') for e in profiles.iter('Profile')}
pools={p.get('id') for p in ET.parse(root/'LSNPCDatabase.xml').getroot().find('DispatchPedPools')}
branch_ids={e.get('id') for e in ET.parse(root/'LSPDDispatchEvent.xml').getroot().iter('Branch')}
for event in events:
    for profile in event.get('criminalProfileIds','').split(','):assert profile in profile_ids,(event.get('id'),profile)
    for branch in event.get('branchIds','').split(','):assert branch in branch_ids,(event.get('id'),branch)
    for name in ['victimPool','sceneParticipantPool']:
        if event.get(name):assert event.get(name) in pools
    assert int(event.get('maximumSuspects',event.get('suspectCount','1')))<=5
    if event.get('preparedActivity') in ['driving_fast','driving_slow','ramming']:
        assert event.get('requiresVehicle')=='true' and event.get('mobile')=='true'
assets={e.get('id') for tag in ['Peds','Vehicles','Weapons'] for e in profiles.find(tag)}
for pool in profiles.find('AssetPools'):
    for ref in pool: assert ref.get('id') in assets,(pool.get('id'),ref.get('id'))
audio=ET.parse(root/'ResponseAudio.xml').getroot()
audio_events={e.get('id'):e for e in audio.iter('Event')}
missing=[]
for event in audio_events.values():
    if event.get('reuseEvent'):
        target=audio_events[event.get('reuseEvent')]
        assert list(target.iter('Clip')) and target.get('speaker')==event.get('speaker')
    for clip in event.findall('Clip'):
        if clip.get('status')=='ready' and not (root/'Audio'/clip.get('file').replace('\\','/')).is_file():missing.append(clip.get('file'))
baseline_audio=ET.fromstring(subprocess.check_output(['git','show','HEAD:ResponseAudio.xml'],cwd=root))
baseline_missing={c.get('file') for c in baseline_audio.iter('Clip') if c.get('status')=='ready' and not (root/'Audio'/c.get('file').replace('\\','/')).is_file()}
assert set(missing)<=baseline_missing,missing
print(f'Existing missing recordings: {len(missing)} (no new nonexistent WAV references)')
# Original catalog IDs remain present. The original revision is recorded in the handoff.
base='frozen-baseline'  # use the manifest base SHA or HEAD before publishing
import json
manifest=root/'DispatchExpansionManifest.json'
if manifest.exists():base=json.loads(manifest.read_text())['base_commit']
else:base='HEAD'
old=ET.fromstring(subprocess.check_output(['git','show',base+':LSPDDispatchEvent.xml'],cwd=root))
assert {e.get('id') for e in old.iter('Event')} <= {e.get('id') for e in events}
for filename in ['LSPDCrimeActivity.cs','LSPDCrimeActivityEvent.cs','LSPDCrimeActivityEvent.xml','LSPDConvoy.cs','LSImmersiveLocation.xml']:
    assert subprocess.check_output(['git','show',base+':'+filename],cwd=root)==(root/filename).read_bytes(),filename+' unexpectedly changed'
print(f'PASS: {len(events)} events, catalog references, original IDs, audio files, and separate activity sources')
