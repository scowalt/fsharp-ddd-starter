def positive: type == "number" and . == floor and . > 0 and . <= 1125899906842624;
def identity: type == "string" and test("^[a-z0-9][a-z0-9-]{0,62}$");
def path: type == "string" and startswith("/") and (test("[\\x00-\\x1f\\x7f]") | not);
(keys | sort) == (["app", "environment", "database", "staging_directory", "state_directory", "repository", "repository_id", "remote_budget_bytes", "local_budget_bytes", "lock_wait_seconds", "operation_timeout_seconds"] | sort)
and (.app | identity) and (.environment | identity)
and (.database | path) and (.staging_directory | path) and (.state_directory | path)
and (.repository_id | type == "string" and test("^[a-f0-9]{64}$"))
and (.remote_budget_bytes | positive) and (.local_budget_bytes | positive)
and (.lock_wait_seconds | positive and . <= 3600)
and (.operation_timeout_seconds | positive and . <= 86400)
and (.repository |
  if .type == "local" then (keys | sort) == ["path", "type"] and (.path | path)
  elif .type == "r2" then
    (keys | sort) == ["account_id", "bucket", "type"]
    and (.account_id | type == "string" and test("^[a-f0-9]{32}$"))
    and (.bucket | type == "string" and test("^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$"))
  else false end)
