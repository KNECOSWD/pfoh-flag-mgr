const bioLimit = /Bio must be \d+ characters or fewer\./;

function entriesOf(errors) {
  if (!errors || typeof errors !== "object") return [];
  return Object.entries(errors);
}

export function chooseServerMessage(parsed, rawMessage) {
  const entries = entriesOf(parsed?.errors);
  const bioMessages = entries
    .filter(([field]) => {
      const name = String(field).toLowerCase();
      return name === "description" || name.endsWith(".description");
    })
    .flatMap(([, messages]) => (Array.isArray(messages) ? messages : []))
    .map((message) => String(message))
    .filter((message) => bioLimit.test(message));

  if (bioMessages.length > 0) {
    return bioMessages.join("; ");
  }

  const validationErrors = entries
    .flatMap(([field, messages]) => (Array.isArray(messages) ? messages : []).map((message) => `${field}: ${message}`))
    .join("; ");

  return parsed?.message || parsed?.detail || parsed?.title || validationErrors || rawMessage;
}
