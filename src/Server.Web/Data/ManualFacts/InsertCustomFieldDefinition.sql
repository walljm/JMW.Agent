INSERT INTO custom_field_definitions
    (label, slug, target_view_title, target_view_group, is_new_view, created_by)
VALUES
    ($1, $2, $3, $4, $5, $6)
ON CONFLICT (slug) DO NOTHING
RETURNING id, label, slug, target_view_title, target_view_group, is_new_view, created_at, created_by
