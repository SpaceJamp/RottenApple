import os
import tempfile
import unittest
from unittest import mock

import rottenapple


class Tests(unittest.TestCase):
    def test_list_devices(self):
        replies = {
            ("idevice_id", "-l"): "abc123 (USB)\n\n",
            ("ideviceinfo", "-u", "abc123", "-k", "DeviceName"): "Jo's iPhone",
            ("ideviceinfo", "-u", "abc123", "-k", "ProductVersion"): "",
        }
        with mock.patch.object(rottenapple, "run_quiet", lambda *c: replies.get(c, "")):
            self.assertEqual(rottenapple.list_devices(), [("abc123", "Jo's iPhone  iOS ?  [abc123]")])

    def test_is_backup_dir(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertFalse(rottenapple.is_backup_dir(d))
            for f in ("Info.plist", "Manifest.plist"):
                open(os.path.join(d, f), "w").close()
            self.assertTrue(rottenapple.is_backup_dir(d))


if __name__ == "__main__":
    unittest.main()
