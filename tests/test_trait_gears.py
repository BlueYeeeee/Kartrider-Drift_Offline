"""Exercise trait-kart allocation and legacy repair through the offline C# RPC."""
import copy
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import unittest

import test_release


class TraitGearTests(unittest.TestCase):
    setUp = test_release.ReleaseTests.setUp
    close = test_release.ReleaseTests.close
    rpc = test_release.ReleaseTests.rpc
    karts = ('Kart:401010060', 'Kart:401010061', 'Kart:401010068')
    trait = 'DriftGaugePreservePercent'

    def apply(self, kind, kart, settings=None):
        request = dict(_type=kind, kartItemId=kart)
        if settings is not None:
            request['gearSettings'] = settings
        return self.rpc(op='apply', performance=True, request=request,
                        response=dict(_type='Pr' + kind[2:], result=dict(code=0)))

    def test_seed_keeps_captured_locked_traits_without_spending_gears(self):
        templates = json.loads((test_release.ROOT / 'resources/release/responses.json').read_text(encoding='utf8'))
        for state in (json.loads(templates['PqLogin']), self.state):
            for kart in self.karts:
                info = state['ownKartsUpgradeInfo'][kart]['gearInfo']
                self.assertEqual(info['gearSettings'][self.trait], 5)
                self.assertEqual(info['remainGearCount'], 51)

    def test_trait_karts_save_reset_and_reject_locked_changes(self):
        for kart in self.karts:
            with self.subTest(kart=kart):
                settings = copy.deepcopy(self.state['ownKartsUpgradeInfo'][kart]['gearInfo']['gearSettings'])
                settings['DriftBoostFactorSpeed'] = 10
                reply = self.apply('PqKartGearSet', kart, settings)
                self.assertEqual(reply['remainGears'], 41)
                self.assertEqual(reply['gearSettings'][self.trait], 5)
                self.apply('PqKartGearSet', kart, dict(AutoUseBoost=3))
                for value in (0, 1, -5, 6):
                    before = self.rpc(op='snapshot')
                    op = dict(op='apply', performance=True,
                              request=dict(_type='PqKartGearSet', kartItemId=kart, gearSettings={self.trait: value}),
                              response=dict(_type='PrKartGearSet', result=dict(code=0)))
                    self.proc.stdin.write(json.dumps(op) + '\n')
                    self.proc.stdin.flush()
                    self.assertFalse(json.loads(self.proc.stdout.readline())['ok'])
                    self.assertEqual(self.rpc(op='snapshot'), before)
                reply = self.apply('PqKartGearReset', kart)
                self.assertEqual(reply['remainGears'], 51)
                self.assertEqual(reply['gearSettings'][self.trait], 5)
                self.assertEqual(reply['gearSettings']['DriftBoostFactorSpeed'], 0)
        self.apply('PqKartGearSet', 'Kart:401010041', {self.trait: 2})

    def test_legacy_repair_preserves_other_tuning_and_is_idempotent(self):
        legacy = self.rpc(op='snapshot')
        for kart in self.karts:
            info = legacy['ownKartsUpgradeInfo'][kart]['gearInfo']
            info['gearSettings'].update({self.trait: 0, 'BoosterAccel': 2})
            info['remainGearCount'] = 49
        with closing(sqlite3.connect(Path(self.folder.name) / 'player.sqlite3')) as db:
            db.execute('UPDATE profile SET data=? WHERE id=1', (json.dumps(legacy),))
            db.commit()
        expected = copy.deepcopy(legacy)
        for kart in self.karts:
            expected['ownKartsUpgradeInfo'][kart]['gearInfo']['gearSettings'][self.trait] = 5
        for _ in range(2):
            self.assertEqual(self.rpc(op='init'), expected)
        for kart in self.karts:
            settings = expected['ownKartsUpgradeInfo'][kart]['gearInfo']['gearSettings']
            self.apply('PqKartGearSet', kart, settings)
