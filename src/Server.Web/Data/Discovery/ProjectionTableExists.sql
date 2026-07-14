-- Existence check via information_schema (not to_regclass): the validator probes
-- with an empty parameter, and to_regclass('jmwdiscovery.') raises 42602. A catalog
-- lookup returns false for an unknown/empty name instead of erroring.
SELECT
    exists
        (
            SELECT
                1
            FROM
                information_schema.tables
            WHERE
                  table_schema = 'jmwdiscovery'
              AND table_name = $1
            ) AS EXISTS
