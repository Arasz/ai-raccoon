# Screaming architecture

Organize folders and modules by domain/business concept, not by generic technical bucket. A new folder name should tell a reader what the system *does*, not what kind of file lives there — avoid catch-all `Services/`, `Controllers/`, `Utils/` buckets in favor of concept-named ones. A shared technical chassis (logging, DI wiring, cross-cutting middleware) is the one accepted exception.
