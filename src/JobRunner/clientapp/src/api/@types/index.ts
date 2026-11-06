/* eslint-disable */
export type AttemptPage = {
  unitPath: string;
  currentAttemptId: string | null;
  archived: boolean;
  items: JobAttempt[];
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
}

export type EditorEdge = {
  from: string;
  to: string;
  conditional: boolean;
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
}

export type ProblemDetails = {
  type?: string | null | undefined;
  title?: string | null | undefined;
  status?: number | null | undefined;
  detail?: string | null | undefined;
  instance?: string | null | undefined;
}

export type SchedulePoint = {
  rule?: number | undefined;
  businessDate?: string | undefined;
  due?: string | undefined;
}

export type SourceEvidence = {
  mapping?: DefinitionSource | undefined;
  contentHash?: string | undefined;
  eTag?: string | undefined;
  base64?: string | undefined;
}
