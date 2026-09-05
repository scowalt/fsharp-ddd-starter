terraform {
  required_version = ">= 1.12.6, < 2.0.0"

  required_providers {
    cloudflare = {
      source  = "cloudflare/cloudflare"
      version = "5.18.0"
    }
  }
}

# Infrastructure credentials come from CLOUDFLARE_API_TOKEN, not this file.
provider "cloudflare" {}

resource "cloudflare_r2_bucket" "backups" {
  account_id    = var.cloudflare_account_id
  name          = var.bucket_name
  storage_class = "Standard"

  lifecycle {
    prevent_destroy = true
  }
}

resource "cloudflare_r2_managed_domain" "backups" {
  account_id  = var.cloudflare_account_id
  bucket_name = cloudflare_r2_bucket.backups.name
  enabled     = false
}

resource "cloudflare_r2_bucket_lifecycle" "backups" {
  account_id  = var.cloudflare_account_id
  bucket_name = cloudflare_r2_bucket.backups.name

  rules = [{
    id         = "abort-abandoned-multipart-uploads"
    enabled    = true
    conditions = { prefix = "" }
    abort_multipart_uploads_transition = {
      condition = {
        type    = "Age"
        max_age = 86400
      }
    }
  }]

  # Never age-expire restic objects: recent snapshots can reference old packs.
}
