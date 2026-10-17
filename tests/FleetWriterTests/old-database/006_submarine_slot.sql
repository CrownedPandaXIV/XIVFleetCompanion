-- Gives each stored submarine its workshop slot (1-4), so a sub keeps its place when it is renamed.
-- Run once, before or after updating the plugin to 0.5.0 (safe to run more than once; instant).
--
-- Plugin 0.5.0 fills the slot from AutoRetainer's list of subs; the app (0.8.0) then finds each slot's
-- sub by slot instead of by the default name "Submersible-N". Existing rows get their slot from the
-- default name now; a renamed sub gets its slot at the next sync.

ALTER TABLE companion_submarine_snapshot ADD COLUMN IF NOT EXISTS slot int;

UPDATE companion_submarine_snapshot
SET slot = substring(sub_name FROM '^Submersible-([1-4])$')::int
WHERE slot IS NULL AND sub_name ~ '^Submersible-[1-4]$';
