using VilarDriverApi.Models;

namespace VilarDriverApi.Data
{
    public static class DbSeeder
    {
        public static void Seed(AppDbContext db)
        {
            // =========================================================
            // DEPLOY-SAFE SEED
            // - nie seedujemy zleceń demo
            // - nie seedujemy driverów demo
            // - nie nadpisujemy żadnych haseł
            // - admin ma istnieć; jeśli istnieje -> tylko dopilnuj roli
            // =========================================================

            var admin = db.Users.FirstOrDefault(u => u.Login == "admin");

            if (admin == null)
            {
                // Admin powinien być już utworzony w Twojej bazie.
                // Jeśli kiedyś postawisz nową bazę i chcesz tworzyć admina automatycznie,
                // zrób to świadomie (np. ENV SEED_ADMIN_PASSWORD) – ale teraz nie ryzykujemy.
                throw new InvalidOperationException(
                    "Admin user (login='admin') not found. Refusing to create admin automatically to avoid accidental default credentials.");
            }

            // Dopilnuj roli admin (hasła nie ruszamy!)
            if (admin.Role != UserRole.Admin)
            {
                admin.Role = UserRole.Admin;
                db.SaveChanges();
            }

            // ✅ brak demo orders
            // ✅ brak demo drivers
        }
    }
}
