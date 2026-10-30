"""Standalone source-release checks against the real C# RPC; no game started."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
EXE = Path(os.environ.get('KARTDRIFT_TEST_EXE', ROOT / 'build/client/KartDriftLocal.exe'))


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.proc = subprocess.Popen([str(EXE), '--core-rpc', str(Path(self.folder.name) / 'player.sqlite3')],
                                     stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                     text=True, encoding='utf8')
        self.addCleanup(self.close)
        self.state = self.rpc(op='init')

    def close(self):
        self.proc.stdin.close()
        self.proc.wait(timeout=10)
        self.proc.stdout.close()
        self.proc.stderr.close()

    def rpc(self, **value):
        self.proc.stdin.write(json.dumps(value) + '\n')
        self.proc.stdin.flush()
        line = self.proc.stdout.readline()
        self.assertTrue(line, 'C# RPC exited unexpectedly')
        reply = json.loads(line)
        self.assertTrue(reply['ok'], reply)
        return reply['result']

    def update(self, preset):
        return self.rpc(op='apply', request=dict(_type='PqItemPresetOnUpdate', reqId=1, itemPreset=preset),
                        response=dict(_type='PrPacket', result={'code': 0}))['itemPreset']

    def test_initial_equipment_and_assists(self):
        items = self.state['itemPresetOn']['itemMap']
        for slot, value in dict(KartBooster='Kart:407000001', PartPlate='Kart:408000005',
                                SkidMark='Kart:411000001', MotionFinish='Motion:343010002',
                                IngameEmoticon4='emoticon:801000004').items():
            self.assertEqual(items[slot], value)
        option = self.state['optionSetting']['gameOptions']['DriveAssistOption']
        for switch in ['AutoDrive', 'AutoForward', 'FallBarrier', 'CornerBarrier', 'ItemAutoDrift',
                       'SpeedAutoDrift', 'TrackGuide', 'DriftUse', 'DriftBoosterUse', 'BoosterUse',
                       'LandingPoint', 'AutoLongSlideDrift']:
            self.assertIn(switch + '=0;', option)

    def test_exclusive_parts_and_custom_selection(self):
        preset = self.update({'itemMap': {'Kart': 'Kart:401000274', 'Character': 'Character:100000043'}})
        self.assertEqual(preset['itemMap']['KartBooster'], 'Kart:407000065')
        self.assertEqual(preset['itemMap']['PartPlate'], 'Kart:408000334')
        preset['itemMap'].update(KartBooster='Kart:407000003', PartWheel='Kart:406000132',
                                MotionFinish='Motion:343010007', IngameEmoticon1='emoticon:801000016')
        selected = self.update(preset)
        self.assertEqual(selected, preset)
        self.update({'itemMap': {'Kart': 'Kart:401010019'}})
        restored = self.update({'itemMap': {'Kart': 'Kart:401000274'}})
        self.assertEqual(restored['itemMap']['KartBooster'], 'Kart:407000003')
        self.assertEqual(restored['itemMap']['PartWheel'], 'Kart:406000132')

    def test_new_character_and_body_parts(self):
        preset = self.update({'itemMap': {'Kart': 'Kart:401000001', 'Character': 'Character:100000001'}})
        self.assertEqual(preset['itemMap']['PartFront'], 'Kart:403010001')
        self.assertEqual(preset['itemMap']['MotionFinish'], 'Motion:301010002')
        preset['itemMap'].update(Kart='Kart:401000274', Character='Character:100000043')
        changed = self.update(preset)['itemMap']
        self.assertNotIn('PartFront', changed)
        self.assertEqual(changed['MotionFinish'], 'Motion:343010002')

    def test_localization_data_and_template_hash(self):
        manifest = json.loads((ROOT / 'resources/release/manifest.json').read_text(encoding='utf8'))
        raw = (ROOT / 'resources/release/responses.json').read_bytes()
        self.assertEqual(hashlib.sha256(raw).hexdigest(), manifest['templateSha256'])
        self.assertEqual(manifest['version'], '0.2.12')
        templates = json.loads(raw)
        reply = json.loads(templates['PqGetPublicDynData|TextLocalization.json'])
        self.assertEqual(reply['hash'], hashlib.sha256(reply['data'].encode()).hexdigest())
        texts = json.loads(reply['data'])
        self.assertEqual(len(texts), 1469)
        self.assertTrue(all(set(row) == {'zh-hans'} for row in texts.values()))
        self.assertEqual(texts['TrackName2_Forest_R04']['zh-hans'], '\u5d0e\u5c96\u4e0b\u5c71\u9053')

    def test_all_native_tower_files_match_index(self):
        index = json.loads((ROOT / 'resources/data/local-trial-tower.json').read_text(encoding='utf8'))['files']
        self.assertEqual(len(index), 100)
        templates = json.loads((ROOT / 'resources/release/responses.json').read_text(encoding='utf8'))
        reply = json.loads(templates['PqGetPublicDynData|trialTower_default'])
        self.assertEqual(reply['hash'], hashlib.sha256(reply['data'].encode()).hexdigest())
        levels = json.loads(reply['data'])['def']['levels']
        self.assertEqual({row['shadowFileName'] for row in levels}, set(index))
        self.assertTrue(all(row['riderName'].startswith('Item_Costume:') for row in levels))
        for name, expected in index.items():
            self.assertEqual(Path(name).name, name)
            data = (EXE.parent / 'TrialTower' / name).read_bytes()
            self.assertEqual(data[:4], b'GVAS')
            self.assertEqual(len(data), expected['bytes'])
            self.assertEqual(hashlib.sha256(data).hexdigest(), expected['sha256'])


if __name__ == '__main__':
    unittest.main()
