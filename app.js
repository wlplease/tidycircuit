const formatBytes = (bytes) => {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const index = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  return `${(bytes / (1024 ** index)).toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
};

const picker = document.querySelector('#folder-picker');
const panel = document.querySelector('#analysis-panel');
const toast = document.querySelector('#toast');
let toastTimer;
const announce = (message) => {
  toast.textContent = message;
  toast.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toast.classList.remove('show'), 3200);
};

function analyzeFiles(selectedFiles) {
  const files = [...selectedFiles];
  const total = files.reduce((sum, file) => sum + file.size, 0);
  const sorted = [...files].sort((a, b) => b.size - a.size);
  const largest = sorted[0];
  const path = files.find(file => file.webkitRelativePath)?.webkitRelativePath || '';
  const folder = path.split('/')[0] || 'Selected files';
  document.querySelector('#analysis-title').textContent = `A closer look at ${folder}.`;
  document.querySelector('#analysis-subtitle').textContent = 'This overview was created in your browser. The file list was not sent anywhere.';
  document.querySelector('#file-count').textContent = files.length.toLocaleString();
  document.querySelector('#folder-size').textContent = formatBytes(total);
  document.querySelector('#largest-file').textContent = largest ? formatBytes(largest.size) : '—';
  document.querySelector('#metric-storage').textContent = formatBytes(total);
  document.querySelector('#metric-storage-detail').textContent = `${files.length.toLocaleString()} files analyzed · selected folder only`;
  document.querySelectorAll('.metric-status.unknown').forEach(status => {
    if (status.closest('.metric-card')?.querySelector('#metric-storage')) {
      status.classList.remove('unknown');
      status.classList.add('local');
      status.innerHTML = '<i></i> ANALYZED LOCALLY';
    }
  });

  const fileList = document.querySelector('#file-list');
  fileList.replaceChildren();
  sorted.slice(0, 8).forEach(file => {
    const row = document.createElement('div');
    row.className = 'file-item';
    const name = document.createElement('span');
    name.className = 'file-item-name';
    name.textContent = file.webkitRelativePath || file.name;
    name.title = name.textContent;
    const size = document.createElement('span');
    size.className = 'file-item-size';
    size.textContent = formatBytes(file.size);
    row.append(name, size);
    fileList.append(row);
  });
  if (!files.length) {
    const empty = document.createElement('div');
    empty.className = 'file-item';
    empty.textContent = 'This folder looks empty, or contains only empty files.';
    fileList.append(empty);
  }

  const groups = new Map();
  for (const file of files) {
    if (!file.size) continue;
    const key = `${file.name.toLocaleLowerCase()}|${file.size}`;
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(file);
  }
  const duplicates = [...groups.values()].filter(group => group.length > 1);
  document.querySelector('#duplicate-count').textContent = duplicates.reduce((sum, group) => sum + group.length - 1, 0).toLocaleString();
  const duplicateList = document.querySelector('#duplicate-list');
  duplicateList.replaceChildren();
  duplicates.slice(0, 6).forEach(group => {
    const row = document.createElement('div');
    row.className = 'file-item';
    const names = document.createElement('span');
    names.className = 'file-item-name';
    names.textContent = `${group[0].name} · ${group.length} matching files`;
    names.title = group.map(file => file.webkitRelativePath || file.name).join('\n');
    const size = document.createElement('span');
    size.className = 'file-item-size';
    size.textContent = `${formatBytes(group[0].size)} each`;
    row.append(names, size);
    duplicateList.append(row);
  });
  if (!duplicates.length) {
    const empty = document.createElement('div');
    empty.className = 'file-item';
    empty.textContent = 'No matching name-and-size candidates in this selection.';
    duplicateList.append(empty);
  }

  const categories = new Map();
  for (const file of files) {
    const extension = file.name.includes('.') ? file.name.split('.').pop().toLocaleLowerCase() : '(no extension)';
    categories.set(extension, (categories.get(extension) || 0) + 1);
  }
  const organizeList = document.querySelector('#organize-list');
  organizeList.replaceChildren();
  [...categories.entries()].sort((a, b) => b[1] - a[1]).slice(0, 5).forEach(([extension, count]) => {
    const row = document.createElement('div');
    row.className = 'file-item';
    row.innerHTML = `<span class="file-item-name"></span><span class="file-item-size"></span>`;
    row.firstElementChild.textContent = extension === '(no extension)' ? 'Files without an extension' : `.${extension} files`;
    row.lastElementChild.textContent = `${count.toLocaleString()} ${count === 1 ? 'file' : 'files'}`;
    organizeList.append(row);
  });
  if (!files.length) {
    const empty = document.createElement('div');
    empty.className = 'file-item';
    empty.textContent = 'Choose a folder containing files to get organization ideas.';
    organizeList.append(empty);
  }
  panel.hidden = false;
  panel.scrollIntoView({ behavior: 'smooth', block: 'start' });
}

document.querySelector('#choose-folder').addEventListener('click', () => picker.click());
document.querySelector('#scan-button').addEventListener('click', () => picker.click());
document.querySelector('.rec-folder').addEventListener('click', () => picker.click());
document.querySelector('#choose-another').addEventListener('click', () => picker.click());
document.querySelectorAll('[data-action="folder"]').forEach(button => button.addEventListener('click', () => picker.click()));
picker.addEventListener('change', () => {
  if (picker.files?.length) analyzeFiles(picker.files);
  picker.value = '';
});
document.querySelector('#close-analysis').addEventListener('click', () => { panel.hidden = true; });
document.querySelector('#dismiss-notice').addEventListener('click', (event) => event.currentTarget.closest('.notice').remove());
document.querySelector('.icon-button[title="Help and information"]').addEventListener('click', () => announce('Choose a folder to view its file count and largest files. Nothing is changed.'));
document.querySelector('.profile-button').addEventListener('click', () => announce('This workspace stays on this device. No account needed.'));

const storageLink = document.querySelector('.guide-button');
storageLink.addEventListener('click', event => {
  event.preventDefault();
  announce('Opening Windows Storage settings. If it does not open, search Windows Settings for “Storage Sense”.');
  window.location.href = storageLink.href;
});

if (!('webkitdirectory' in document.createElement('input'))) {
  document.querySelectorAll('#choose-folder, #scan-button, .rec-folder, #choose-another, [data-action="folder"]').forEach(button => button.addEventListener('click', () => announce('Folder selection is not supported by this browser. Try Chrome or Edge on HTTPS, or use a local file picker.')));
}
