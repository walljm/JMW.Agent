SELECT id, label, slug, target_view_title, target_view_group, is_new_view, created_at, created_by
FROM custom_field_definitions
WHERE slug = $1
