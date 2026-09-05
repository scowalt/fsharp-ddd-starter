variable "cloudflare_account_id" {
  description = "Cloudflare account that owns this application's backup bucket"
  type        = string

  validation {
    condition     = can(regex("^[a-f0-9]{32}$", var.cloudflare_account_id))
    error_message = "Supply a lowercase 32-character hexadecimal Cloudflare account ID."
  }
}

variable "bucket_name" {
  description = "Dedicated application/environment bucket, for example workshop-production-database-backups"
  type        = string

  validation {
    condition = (
      length(var.bucket_name) >= 3 && length(var.bucket_name) <= 63 &&
      can(regex("^[a-z0-9][a-z0-9-]*[a-z0-9]$", var.bucket_name))
    )
    error_message = "Use 3–63 lowercase letters, digits, or hyphens, with no leading or trailing hyphen."
  }
}
