SELECT id, email FROM users WHERE email = @Email;
UPDATE users SET display_name = @DisplayName WHERE id = @Id;
DELETE FROM users WHERE id = @Id;
