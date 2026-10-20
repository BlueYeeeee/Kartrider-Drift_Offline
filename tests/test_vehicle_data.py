"""Verify the real offline executable against the bundled sampled mapping."""
import base64
import copy
import json
import struct
import unittest

from test_release import ROOT
import test_release


class VehicleDataTests(unittest.TestCase):
    setUp = test_release.ReleaseTests.setUp
    close = test_release.ReleaseTests.close
    rpc = test_release.ReleaseTests.rpc
    update = test_release.ReleaseTests.update

    @classmethod
    def setUpClass(cls):
        data = ROOT / 'resources/data'
        cls.mapping = json.loads((data / 'local-sampled-speed-gear-map.json').read_text(encoding='utf8'))
        cls.vehicles = json.loads((data / 'local-speed-bodies.json').read_text(encoding='utf8'))['vehicles']
        cls.layout = json.loads((data / 'local-body-layout.json').read_text(encoding='utf8'))
        cls.fields = {f['name']: f for f in cls.layout}

    def body(self, kart, upgrade):
        return base64.b64decode(self.rpc(op='body', kart=kart, upgrade=upgrade))

    def effective(self, raw):
        return b''.join(raw[f['offset']:f['offset'] + f['size']] for f in self.layout)

    def reset_upgrade(self, kart):
        upgrade = copy.deepcopy(self.vehicles[kart]['upgrade'])
        profile = self.mapping['profiles'][self.mapping['vehicles'][kart][1]]
        upgrade['gearInfo']['gearSettings'].update({gear: 0 for gear in profile})
        return upgrade

    def test_all_249_reset_bodies_and_two_fallbacks(self):
        self.assertEqual(len(self.mapping['vehicles']), 249)
        self.assertEqual(len(self.mapping['baselines']), 77)
        self.assertEqual(len(self.mapping['tables']), 9)
        self.assertEqual(len(self.mapping['profiles']), 5)
        for kart, (baseline, _) in self.mapping['vehicles'].items():
            self.assertEqual(self.effective(self.body(kart, self.reset_upgrade(kart))),
                             self.effective(bytes.fromhex(self.mapping['baselines'][baseline])), kart)
        fallback = set(self.vehicles) - set(self.mapping['vehicles'])
        self.assertEqual(len(fallback), 2)
        for kart in fallback:
            source = self.vehicles[kart]
            self.assertEqual(self.effective(self.body(kart, source['upgrade'])),
                             self.effective(bytes.fromhex(source['rawBodyHex'])), kart)

    def test_every_shared_table_level_and_rounding_profile(self):
        checked = set()
        for kart, (baseline, profile_id) in self.mapping['vehicles'].items():
            for gear, table_id in self.mapping['profiles'][profile_id].items():
                if table_id in checked:
                    continue
                checked.add(table_id)
                for level, changes in self.mapping['tables'][table_id]['levels'].items():
                    upgrade = self.reset_upgrade(kart)
                    upgrade['gearInfo']['gearSettings'][gear] = int(level)
                    expected = bytearray.fromhex(self.mapping['baselines'][baseline])
                    for name, (operation, delta) in changes.items():
                        offset = self.fields[name]['offset']
                        if operation == 'f32_add':
                            value = struct.unpack_from('<f', expected, offset)[0]
                            struct.pack_into('<f', expected, offset, value + struct.unpack('<f', bytes.fromhex(delta))[0])
                        elif operation == 'u32_add':
                            value = struct.unpack_from('<I', expected, offset)[0]
                            struct.pack_into('<I', expected, offset, (value + delta) & 0xffffffff)
                        elif operation == 'or':
                            expected[offset] = int(bool(expected[offset] or delta))
                        else:
                            self.fail(operation)
                    self.assertEqual(self.effective(self.body(kart, upgrade)), self.effective(expected),
                                     (kart, gear, level))
        self.assertEqual(checked, set(self.mapping['tables']))

    def test_unknown_levels_fixed_changes_and_engines_are_rejected(self):
        kart = 'Kart:401010017'
        for change in ('level', 'fixed', 'engine'):
            upgrade = self.reset_upgrade(kart)
            if change == 'level':
                upgrade['gearInfo']['gearSettings']['DriftBoostFactorSpeed'] = 10
            elif change == 'fixed':
                upgrade['gearInfo']['gearSettings']['TransAccelFactor'] += 1
            else:
                upgrade['engineType'] = 999
            self.proc.stdin.write(json.dumps(dict(op='body', kart=kart, upgrade=upgrade)) + '\n')
            self.proc.stdin.flush()
            reply = json.loads(self.proc.stdout.readline())
            self.assertFalse(reply['ok'], change)
            self.assertEqual(reply['kind'], 'PerformanceUnavailable')

    def test_selected_focus_wheel_survives_switch_and_reload(self):
        selected = self.update({'itemMap': {'Kart': 'Kart:401010071', 'PartWheel': 'Kart:406010065'}})
        self.assertEqual(selected['itemMap']['PartWheel'], 'Kart:406010065')
        self.update({'itemMap': {'Kart': 'Kart:401010017'}})
        restored = self.update({'itemMap': {'Kart': 'Kart:401010071'}})
        self.assertEqual(restored['itemMap']['PartWheel'], 'Kart:406010065')

    def test_offline_source_and_embedded_bundle_exclude_experiments(self):
        import zipfile
        forbidden = ('multiplayer', 'remote_kart', 'native_shared', 'room_game', 'netcode',
                     'localinstance', 'localendpoint', 'native_race_equipment', 'native_hud_retry')
        files = list((ROOT / 'launcher-csharp').glob('*.cs'))
        for path in files:
            self.assertFalse(any(word in path.name.lower() for word in forbidden), path.name)
            text = path.read_text(encoding='utf-8-sig').lower()
            self.assertNotIn('multiplayerroom', text, path.name)
            self.assertNotIn('localendpoint.http', text, path.name)
        with zipfile.ZipFile(ROOT / 'LocalData.zip') as bundle:
            for name in bundle.namelist():
                self.assertFalse(any(word in name.lower() for word in forbidden), name)
            self.assertIn('data/local-sampled-speed-gear-map.json', bundle.namelist())


if __name__ == '__main__':
    unittest.main()
