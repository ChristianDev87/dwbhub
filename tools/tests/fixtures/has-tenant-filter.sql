SELECT id, email FROM users WHERE tenant_id = @TenantId AND email = @Email;
UPDATE users SET display_name = @DisplayName WHERE id = @Id AND tenant_id = @TenantId;
INSERT INTO users (tenant_id, email, password_hash) VALUES (@TenantId, @Email, @Hash);
DELETE FROM users WHERE id = @Id AND tenant_id = @TenantId;
