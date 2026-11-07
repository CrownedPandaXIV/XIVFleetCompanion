-- App 1.7.0: the Discord alerts are removed.
--
-- Their one setting the app still uses, how far behind its account a character must be to count as no longer synced
-- (it was "Plugin not syncing after N minutes"), keeps its value under its own name; Settings → General → Warnings
-- shows it. Their other settings are removed. Safe to run twice: the second time there is nothing left to move.
INSERT INTO fleet_economics_settings (setting_key, setting_value, updated_at)
SELECT 'not_synced_after_minutes', setting_value, now()
FROM fleet_economics_settings
WHERE setting_key = 'alert_sync_stale_minutes' AND setting_value IS NOT NULL
ON CONFLICT (setting_key) DO NOTHING;

DELETE FROM fleet_economics_settings WHERE setting_key LIKE 'alert\_%';
