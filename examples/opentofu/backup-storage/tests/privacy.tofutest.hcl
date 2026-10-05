mock_provider "cloudflare" {}

variables {
  cloudflare_account_id = "00000000000000000000000000000000"
  bucket_name           = "example-production-database-backups"
}

run "private_standard_repository" {
  command = plan

  assert {
    condition     = cloudflare_r2_bucket.backups.storage_class == "Standard"
    error_message = "Short-lived deployment backups must use Standard storage."
  }

  assert {
    condition     = cloudflare_r2_managed_domain.backups.enabled == false
    error_message = "The backup bucket must not expose a public r2.dev domain."
  }

  assert {
    condition = (
      length(cloudflare_r2_bucket_lifecycle.backups.rules) == 1 &&
      cloudflare_r2_bucket_lifecycle.backups.rules[0].enabled &&
      cloudflare_r2_bucket_lifecycle.backups.rules[0].conditions.prefix == "" &&
      cloudflare_r2_bucket_lifecycle.backups.rules[0].abort_multipart_uploads_transition.condition.max_age == 86400
    )
    error_message = "Abandoned multipart uploads must be cleaned across the bucket after one day."
  }

  assert {
    condition = (
      cloudflare_r2_bucket_lifecycle.backups.rules[0].delete_objects_transition == null &&
      cloudflare_r2_bucket_lifecycle.backups.rules[0].storage_class_transitions == null
    )
    error_message = "Lifecycle rules must not expire or change the class of restic repository objects."
  }

  assert {
    condition     = output.restic_repository == "s3:https://00000000000000000000000000000000.r2.cloudflarestorage.com/example-production-database-backups/database"
    error_message = "The dedicated repository must use the application's private bucket and database prefix."
  }
}

run "reject_invalid_account" {
  command = plan
  variables {
    cloudflare_account_id = "not-an-account"
  }
  expect_failures = [var.cloudflare_account_id]
}

run "reject_invalid_bucket" {
  command = plan
  variables {
    bucket_name = "Public_Backups"
  }
  expect_failures = [var.bucket_name]
}
