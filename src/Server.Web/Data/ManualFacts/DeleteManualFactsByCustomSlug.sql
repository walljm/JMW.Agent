-- Cascade for deleting a custom_field_definitions row: removes every device's history for that
-- slug. Scoped to source = ManualEntry (never touches collector history — collectors never
-- write into the Custom[] dimension in the first place, but the guard is kept anyway).
DELETE FROM facts_history
WHERE attribute_path = 'Device[].Custom[].Value'
  AND key_values ->> 'Custom' = $1
  AND source = $2
RETURNING id
