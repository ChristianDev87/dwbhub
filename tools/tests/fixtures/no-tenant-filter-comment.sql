-- DWBHUB-NO-TENANT-FILTER: tenants table is the root, has no tenant_id of its own.
SELECT id, name, slug FROM tenants WHERE slug = @Slug;
