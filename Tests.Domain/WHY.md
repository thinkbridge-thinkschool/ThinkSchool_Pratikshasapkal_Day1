# WHY

Moving from an anemic model to a rich domain model made the 'Quote' entity responsible for protecting its own rules and consistency. Earlier, the entity was just a simple object with public setters, so validation depended entirely on controllers or external code. That approach is risky because it’s easy for one endpoint, future feature, or background process to forget validation and accidentally save invalid data.

With the rich model, every quote must be created through 'Quote.Create(author, text)'. This means invalid quotes simply cannot exist in the system. The validation is centralized in one place instead of being repeated across multiple layers of the application.

Making 'Text' immutable after creation was also important. A quote is supposed to represent a fixed statement, so allowing text changes later could create data integrity issues or accidental modifications. By removing public setters and controlling behavior inside the entity, the model guarantees that quote content stays stable after creation.

Adding soft delete with an 'IsDeleted' flag also improves safety because records are preserved for auditing and recovery instead of being permanently removed.

One realistic bug the old anemic model could have caused is saving an empty or whitespace-only quote because a controller forgot validation. In the rich model, that bug is prevented automatically because the entity itself refuses invalid creation requests.