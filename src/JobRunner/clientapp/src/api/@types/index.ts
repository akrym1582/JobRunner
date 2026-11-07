/* eslint-disable */
export type Activation = {
  formatVersion?: number | undefined;
  id?: string | undefined;
  revision?: number | undefined;
  definitionHash?: string | undefined;
  settingsHash?: string | undefined;
  phase?: string | undefined;
  baseline?: string | undefined;
  groupCatalogHash?: string | null | undefined;
}

export type AttemptPage = {
  unitPath: string;
  currentAttemptId: string | null;
  archived: boolean;
  items: JobAttempt[];
}

export type CalendarEvaluation = {
  working?: boolean | undefined;
  sourcePath?: string | null | undefined;
  rule?: string | undefined;
}

export type Catalog = {
  namespace: string;
  definitionHash: string;
  timeZone: string;
  phase: string;
  networks: Network[];
}

export type DefinitionSource = {
  kind?: string | undefined;
  targetPath?: string | undefined;
  blobPath?: string | undefined;
  encoding?: string | undefined;
}

export type DelayMonitorSettings = {
  start?: string | null | undefined;
  startMinutes?: number | null | undefined;
  end?: string | null | undefined;
  endMinutes?: number | null | undefined;
  duration?: boolean | null | undefined;
  durationMinutes?: number | null | undefined;
}

export type Edge = {
  id: string;
  from: string;
  to: string;
  kind: string;
}

export type EditorAttributes = {
  id: string;

  attributes: {
    [key: string]: string[];
  };

  type?: string | null | undefined;
}

export type EditorCalendarDay = {
  date: string;
  evaluation: CalendarEvaluation;
}

export type EditorCalendarRequest = {
  draft: EditorDraft;
  unitId: string;
  month: string;
}

export type EditorContainer = {
  sourceIndex: number;
  parentPath: string;
}

export type EditorDocument = {
  draft: EditorDraft;
  sources: EditorSource[];
  containers?: EditorContainer[] | null | undefined;
  timeZone?: string | null | undefined;
  scheduleTimeMode?: number | null | undefined;
}

export type EditorDraft = {
  definitionHash: string;
  networks: EditorNetwork[];
  editorVersion?: number | null | undefined;
  roots?: EditorRoot[] | null | undefined;
  schedules?: EditorAttributes[] | null | undefined;
  calendars?: EditorAttributes[] | null | undefined;
  localSources?: SourceEvidence[] | null | undefined;
  execution?: EditorAttributes[] | null | undefined;
  newGroups?: EditorGroupCreation[] | null | undefined;
  newFiles?: EditorFileCreation[] | null | undefined;
  groupChanges?: EditorGroupChange[] | null | undefined;
  networkChanges?: EditorNetworkChange[] | null | undefined;
  workingSources?: SourceEvidence[] | null | undefined;
  workingContainers?: EditorContainer[] | null | undefined;

  groupOrigins?: {
    [key: string]: string;
  } | null | undefined;
}

export type EditorEdge = {
  from: string;
  to: string;
  conditional: boolean;
}

export type EditorFileCreation = {
  fileName: string;
  targetPath: string;
  encoding: string;
  bom: boolean;
  groupName: string;
}

export type EditorGroupChange = {
  id: string;
  name?: string | null | undefined;
  delete?: boolean | undefined;
}

export type EditorGroupCreation = {
  sourceIndex: number;
  parentPath: string;
  name: string;
}

export type EditorGroupPackageRequest = {
  draft: EditorDraft;
  path: string;
  zipBase64: string | null;
  parentPath: string | null;
  name: string | null;
}

export type EditorLocalRequest = {
  sources?: SourceEvidence[] | null | undefined;
  zipBase64?: string | null | undefined;
}

export type EditorNetwork = {
  path: string;
  nodes: EditorNode[];
  edges: EditorEdge[];
}

export type EditorNetworkChange = {
  id: string;
  parentPath?: string | null | undefined;
  type?: string | null | undefined;
}

export type EditorNode = {
  id: string;
  name: string;
  type: string;

  attributes: {
    [key: string]: string[];
  };

  x: number | null;
  y: number | null;
}

export type EditorPasteRequest = {
  draft: EditorDraft;
  copiedDraft: EditorDraft;
  ids: string[];
  targetNetworkId: string | null;
  sourceIndex: number | null;
  parentPath: string | null;
  targetFile: string | null;
}

export type EditorPasteResult = {
  document: EditorDocument;
  clonedIds: string[];
  targetPath: string;
  warnings: string[];
}

export type EditorPreview = {
  valid: boolean;
  errors: string[];
  layoutErrors: string[];
  sources: EditorSource[];
}

export type EditorRoot = {
  node: EditorNode;
  sourceIndex: number;
  parentPath: string;
}

export type EditorSchedulePreview = {
  points: SchedulePoint[];
  timeZone: string;
}

export type EditorScheduleRequest = {
  draft: EditorDraft;
  networkId: string;
  anchor: string;
  from: string;
  until: string;
}

export type EditorSource = {
  fileName: string;
  encoding: string;
  targetPath: string;
  text: string;
  changed: boolean;
  kind?: string | undefined;
}

export type ExecutionPage = {
  items: Occurrence[];
  total: number;
}

export type Graph = {
  unitPath: string;
  definitionHash: string;
  nodes: Node[];
  edges: Edge[];

  root: Occurrence | null;

  canChange: boolean;
  canRestore: boolean;
}

export type GroupStatus = {
  id: string;
  path: string;
  definitionRevision: number;
  control: RuntimeControl;
  pausedBy: string[];
  retired: boolean;
}

export type JobAttempt = {
  id?: string | undefined;
  occurrenceId?: string | undefined;
  owner?: string | undefined;
  epoch?: number | undefined;
  state?: string | undefined;
  processId?: number | null | undefined;
  exitCode?: number | null | undefined;
  startedAt?: string | undefined;
  finishedAt?: string | null | undefined;
  resolutionReason?: string | null | undefined;
  logsComplete?: boolean | undefined;

  outputVariables?: {
    [key: string]: string;
  } | null | undefined;

  statusDetail?: string | null | undefined;
  outputError?: string | null | undefined;
}

export type LogChunk = {
  sequence: number;
  text: string;
}

export type LogPage = {
  chunks: LogChunk[];
  previousCursor: string | null;
  nextCursor: string;
  hasMore: boolean;
  logsComplete: boolean;
  state: string;
}

export type MonthlyEntry = {
  execution: Occurrence;
  status: string;
  executionId: string;
  networkPath: string;
  calendarDate: string;
  comment: string;
  exitCode: number | null;
}

export type MonthlySchedule = {
  month: string;
  timeZone: string;
  units: MonthlyUnit[];
  entries: MonthlyEntry[];
}

export type MonthlyUnit = {
  path: string;
  name: string;
  parent: string;
  type: string;
  comment: string;
  isNetwork: boolean;
}

export type Network = {
  path: string;
  name: string;
  parent: string;
}

export type Node = {
  path: string;
  name: string;
  type: string;
  isNetwork: boolean;
  isRecovery: boolean;
  status: string;

  execution: Occurrence | null;

  exitCode: number | null;
  x: number;
  y: number;
}

export type Occurrence = {
  id?: string | undefined;
  namespace?: string | undefined;
  registrationId?: string | undefined;
  rootId?: string | undefined;
  unitPath?: string | undefined;
  rootPath?: string | undefined;
  rule?: number | undefined;
  businessDate?: string | undefined;
  due?: string | undefined;
  originalDue?: string | undefined;
  state?: string | undefined;
  started?: boolean | undefined;
  attemptId?: string | null | undefined;
  reason?: string | null | undefined;
  finishedAt?: string | null | undefined;
  archivePath?: string | null | undefined;
  jp1ExecutionId?: string | null | undefined;
  materialized?: boolean | null | undefined;
  definitionHash?: string | null | undefined;
  bypassedPredecessors?: string[] | null | undefined;
  standaloneRerun?: boolean | null | undefined;
  scheduleExcluded?: boolean | null | undefined;
  conditionMatched?: boolean | null | undefined;
  statusDetail?: string | null | undefined;
  scheduleChangeId?: string | null | undefined;
  addedByChangeId?: string | null | undefined;
  beforeChangeDue?: string | null | undefined;
  beforeChangeState?: string | null | undefined;
  startedAt?: string | null | undefined;
  restartedAt?: string | null | undefined;
  holdOverride?: boolean | null | undefined;
  beforeChangeHoldOverride?: boolean | null | undefined;
  interruptRequested?: boolean | null | undefined;
  manualExitCode?: number | null | undefined;
  delayMonitor?: DelayMonitorSettings | undefined;
  generationRevision?: number | undefined;
  individuallyModified?: boolean | undefined;
  groupCatalogHash?: string | null | undefined;
}

export type Operation = {
  stateNamespace?: string | undefined;
  id?: string | undefined;
  kind?: string | undefined;
  target?: string | undefined;
  requestHash?: string | undefined;
  status?: string | undefined;
  result?: string | null | undefined;
  attemptId?: string | null | undefined;
  expectedState?: string | null | undefined;
  reason?: string | null | undefined;
  resolution?: string | null | undefined;
  due?: string | null | undefined;
  registrationKind?: string | undefined;
  end?: string | null | undefined;
  createdAt?: string | null | undefined;
  completedAt?: string | null | undefined;
  archivePath?: string | null | undefined;
  holding?: boolean | null | undefined;
  futureGenerations?: number | null | undefined;
  rerunMethod?: string | null | undefined;
  mutationPath?: string | null | undefined;
  mutationApplied?: boolean | null | undefined;
  definitionHash?: string | null | undefined;
  expectedDefinitionHash?: string | null | undefined;
  start?: string | null | undefined;
  fixedDue?: string | null | undefined;
  passedDaemonStarts?: string | null | undefined;
  intervalMinutes?: number | null | undefined;
  intervalUntil?: string | null | undefined;
  rerunOptions?: string[] | null | undefined;
  changeLower?: string | null | undefined;
  pushedAhead?: string | null | undefined;
  relativeMinutes?: number | null | undefined;
  dateType?: string | null | undefined;
  errorCode?: string | null | undefined;
  newStatus?: string | null | undefined;
  newReturnCode?: number | null | undefined;
  delayMonitor?: DelayMonitorSettings | undefined;
  expectedControlRevision?: number | null | undefined;
  expectedDefinitionRevision?: number | null | undefined;
  expectedImpactHash?: string | null | undefined;
  acceptedDefinitionRevision?: number | null | undefined;

  groupOrigins?: {
    [key: string]: string;
  } | null | undefined;
}

export type ProblemDetails = {
  type?: string | null | undefined;
  title?: string | null | undefined;
  status?: number | null | undefined;
  detail?: string | null | undefined;
  instance?: string | null | undefined;
}

export type PublicationGeneration = {
  id: string;
  path: string;
  due: string;
  reason: string | null;
}

export type PublicationPreview = {
  definitionHash: string;
  expectedRevision: number;
  impactHash: string;
  addedRoots: string[];
  removedRoots: string[];
  generations: PublicationGeneration[];
  sources: EditorSource[];
  previousSources: EditorSource[];

  groupOrigins: {
    [key: string]: string;
  } | null;

  groupChanges: string[] | null;
}

export type RuntimeControl = {
  revision?: number | undefined;
  paused?: boolean | undefined;
  pausedAt?: string | null | undefined;
  skipBefore?: string | null | undefined;
}

export type RuntimeStatus = {
  control: RuntimeControl;

  active: Activation | null;

  phase: string;
  pending: Operation[];
}

export type SchedulePoint = {
  rule?: number | undefined;
  businessDate?: string | undefined;
  due?: string | undefined;
}

export type SharedEditorDraft = {
  revision: number;
  baseRevision: number;
  base: EditorDocument;
  draft: EditorDraft;
}

export type SourceEvidence = {
  mapping?: DefinitionSource | undefined;
  contentHash?: string | undefined;
  eTag?: string | undefined;
  base64?: string | undefined;
}
