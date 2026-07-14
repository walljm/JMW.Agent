DELETE FROM custom_field_definitions
WHERE id = $1
RETURNING id, slug
