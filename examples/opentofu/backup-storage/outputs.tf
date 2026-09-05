output "bucket_name" {
  description = "Private bucket for this application's database repository"
  value       = cloudflare_r2_bucket.backups.name
}

output "s3_endpoint" {
  description = "Private S3-compatible endpoint; object credentials are required"
  value       = "https://${var.cloudflare_account_id}.r2.cloudflarestorage.com"
}

output "restic_repository" {
  description = "RESTIC_REPOSITORY value; the endpoint and bucket name are not credentials"
  value       = "s3:https://${var.cloudflare_account_id}.r2.cloudflarestorage.com/${cloudflare_r2_bucket.backups.name}/database"
}
