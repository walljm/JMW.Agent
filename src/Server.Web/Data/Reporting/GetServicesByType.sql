-- Services grouped by type, most common first, for the Services tile breakdown bar.
SELECT
    type
  , count(*) AS COUNT
FROM
    services
GROUP BY
    TYPE
ORDER BY
    COUNT DESC
  , TYPE  ASC
