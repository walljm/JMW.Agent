SELECT
    id
  , occurred_at
  , actor
  , ACTION
  , target_ref
  , detail
FROM
    audit_log
WHERE
    (
    $2::timestamptz IS NULL
  OR occurred_at
   < $2)
 AND (
    $3::bigint IS NULL
  OR id
   < $3)
 AND (
    $4::text IS NULL
  OR ACTION = $4)
 AND (
    $5::text IS NULL
  OR actor = $5)
 AND (
    $6::text IS NULL
  OR target_ref ILIKE '%' || $6 || '%')
 AND (
    $7::timestamptz IS NULL
  OR occurred_at >= $7)
 AND (
    $8::timestamptz IS NULL
  OR occurred_at <= $8)
ORDER BY
    occurred_at DESC
  , id          DESC
    LIMIT $1
