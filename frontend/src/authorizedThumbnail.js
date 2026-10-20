// The image identity is the photo and its url. A new account or MSAL instance
// object on a parent re-render is not a new image.
export function thumbnailLoadKey(photoKey, url) {
  return `${photoKey}\n${url}`;
}

export function thumbnailLoadPlan(previous, props) {
  const key = thumbnailLoadKey(props.photoKey, props.url);
  if (previous && previous.key === key) {
    return { load: false, key, releasePrevious: false };
  }
  return { load: true, key, releasePrevious: Boolean(previous?.held) };
}
