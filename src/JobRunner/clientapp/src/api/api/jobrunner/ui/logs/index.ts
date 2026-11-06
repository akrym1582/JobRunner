/* eslint-disable */
import type { DefineMethods } from 'aspida';
import type * as Types from '../../../../@types';

export type Methods = DefineMethods<{
  get: {
    query: {
      rootId: string;
      occurrenceId: string;
      attemptId: string;
      stream: string;
      cursor?: string | undefined;
    };

    status: 200;
    /** OK */
    resBody: Types.LogPage;
  };
}>;
