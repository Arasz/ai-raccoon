// Small retry-with-backoff helper (E3 fixture corpus: javascript/small).

/**
 * Calls fn, retrying up to maxAttempts times with exponential backoff
 * (baseDelayMs * 2^attempt) between attempts. Rethrows the last error.
 */
async function retryWithBackoff(fn, { maxAttempts = 3, baseDelayMs = 100 } = {}) {
  let lastError;
  for (let attempt = 0; attempt < maxAttempts; attempt += 1) {
    try {
      return await fn();
    } catch (error) {
      lastError = error;
      const delay = baseDelayMs * 2 ** attempt;
      await new Promise((resolve) => setTimeout(resolve, delay));
    }
  }
  throw lastError;
}

module.exports = { retryWithBackoff };
